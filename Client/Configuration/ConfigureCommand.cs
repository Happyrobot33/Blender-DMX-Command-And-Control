using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Client.Worker;
using Client.Rendering;

namespace Client.Configuration;

public static class ConfigureCommand
{
    public static bool IsConfigure(string[] args) => args.Length > 0 && args[0].Equals("configure", StringComparison.OrdinalIgnoreCase);

    public static bool IsShow(string[] args) =>
        args.Length >= 2 && args[0].Equals("config", StringComparison.OrdinalIgnoreCase) && args[1].Equals("show", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(ClientOptions current)
    {
        try
        {
            return await RunCoreAsync(current);
        }
        catch (EndOfStreamException)
        {
            Console.WriteLine();
            Console.Error.WriteLine("Input ended before setup finished; nothing was saved.");
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(ClientOptions current)
    {
        Console.WriteLine("Blender DMX render machine setup");
        Console.WriteLine($"Settings file: {UserSettings.FilePath}");
        Console.WriteLine();

        var section = UserSettings.Load();
        string serverUrl;
        string token;
        while (true)
        {
            serverUrl = Prompt("Server URL", current.ServerUrl, value =>
                Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                    ? null : "Enter a full http:// or https:// address.");

            token = PromptSecret("API token (from the server's Render machines page)", current.ApiToken);

            Console.Write("Checking… ");
            var (ok, message) = await CheckAsync(serverUrl, token);
            Console.WriteLine(message);
            if (ok)
                break;
            if (ConfirmNo("Save anyway?"))
                break;
            Console.WriteLine();
            current.ServerUrl = serverUrl;
        }

        section["ServerUrl"] = serverUrl;
        UserSettings.SetToken(section, token);

        Console.WriteLine();
        section["HNodePath"] = PromptExecutable("HNode executable", current.HNodePath, ApplicationPaths.DefaultHNode);
        section["BlenderPath"] = PromptExecutable("Blender executable", current.BlenderPath, ApplicationPaths.DefaultBlender);

        UserSettings.Save(section);
        Console.WriteLine();
        Console.WriteLine($"Saved to {UserSettings.FilePath}");
        return 0;
    }

    public static void Show(ClientOptions current)
    {
        Console.WriteLine($"Settings file:            {UserSettings.FilePath}{(File.Exists(UserSettings.FilePath) ? "" : " (not created yet)")}");
        Console.WriteLine($"ServerUrl:                {current.ServerUrl}");
        Console.WriteLine($"ApiToken:                 {UserSettings.Mask(current.ApiToken)}");
        Console.WriteLine($"PollIntervalSeconds:      {current.PollIntervalSeconds}");
        Console.WriteLine($"HNodePath:                {ApplicationPaths.Resolve(current.HNodePath, ApplicationPaths.DefaultHNode) ?? $"NOT FOUND ({(string.IsNullOrWhiteSpace(current.HNodePath) ? ApplicationPaths.DefaultHNode() : current.HNodePath)})"}");
        Console.WriteLine($"BlenderPath:              {ApplicationPaths.Resolve(current.BlenderPath, ApplicationPaths.DefaultBlender) ?? $"NOT FOUND ({(string.IsNullOrWhiteSpace(current.BlenderPath) ? ApplicationPaths.DefaultBlender() : current.BlenderPath)})"}");
        Console.WriteLine($"CacheDirectory:           {current.ResolveCacheDirectory()}");
        Console.WriteLine("(Environment variables Emulator__* and --Emulator:* arguments override the settings file.)");
    }

    public static async Task<(bool Ok, string Message)> CheckAsync(string serverUrl, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (false, "no API token entered.");
        using var http = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await http.GetAsync("api/machines/me");
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (false, "the server rejected this API token (wrong token, or the machine is disabled).");
            if (!response.IsSuccessStatusCode)
                return (false, $"unexpected response {(int)response.StatusCode} {response.ReasonPhrase} - is this the right server?");
            var me = await response.Content.ReadFromJsonAsync<MachineIdentity>();
            return (true, $"connected as render machine \"{me?.Name}\".");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, $"couldn't reach {serverUrl} ({ex.Message}).");
        }
        catch (System.Text.Json.JsonException)
        {
            return (false, "the server's response wasn't recognised - is this the right server?");
        }
    }

    private static string Prompt(string label, string? current, Func<string, string?> validate)
    {
        while (true)
        {
            Console.Write(string.IsNullOrEmpty(current) ? $"{label}: " : $"{label} [{current}]: ");
            var input = ReadLineOrThrow().Trim();
            var value = input.Length == 0 ? current ?? string.Empty : input;
            if (validate(value) is { } error)
            {
                Console.WriteLine($"  {error}");
                continue;
            }
            return value;
        }
    }

    private static string PromptExecutable(string label, string? current, Func<string> fallback)
    {
        var suggestion = !string.IsNullOrWhiteSpace(current) ? current : ApplicationPaths.Resolve(null, fallback) ?? fallback();
        return Prompt(label, suggestion, value =>
            ApplicationPaths.Resolve(value, fallback) is not null ? null : $"Not found: {value}");
    }

    private static string PromptSecret(string label, string? current)
    {
        while (true)
        {
            Console.Write(string.IsNullOrEmpty(current) ? $"{label}: " : $"{label} [Enter keeps {UserSettings.Mask(current)}]: ");
            var input = ReadHidden().Trim();
            var value = input.Length == 0 ? current ?? string.Empty : input;
            if (value.Length > 0)
                return value;
            Console.WriteLine("  An API token is required.");
        }
    }

    private static double PromptNumber(string label, double current, double min, double max, bool integer = false)
    {
        var text = Prompt(label, current.ToString(CultureInfo.InvariantCulture), value =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= min && number <= max
            && (!integer || number == Math.Floor(number))
                ? null : $"Enter a {(integer ? "whole number" : "number")} between {min} and {max}.");
        return double.Parse(text, CultureInfo.InvariantCulture);
    }

    private static bool ConfirmNo(string question)
    {
        Console.Write($"{question} [y/N]: ");
        var answer = ReadLineOrThrow().Trim();
        return answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadLineOrThrow() => Console.ReadLine() ?? throw new EndOfStreamException();

    private static string ReadHidden()
    {
        if (Console.IsInputRedirected)
            return ReadLineOrThrow();

        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }
            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
                Console.Write('*');
            }
        }
        Console.WriteLine();
        return buffer.ToString();
    }
}

public record MachineIdentity(int Id, string Name);
