using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Client.Configuration;

public static class UserSettings
{
    public const string Section = "Emulator";
    private static readonly byte[] Entropy = "BlenderDmxClient.ApiToken"u8.ToArray();

    public static string FilePath =>
        Environment.GetEnvironmentVariable("BDMX_CLIENT_SETTINGS") is { Length: > 0 } overridePath
            ? Path.GetFullPath(overridePath)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlenderDmxClient", "settings.json");

    public static JsonObject Load()
    {
        if (!File.Exists(FilePath))
            return new JsonObject();
        var root = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
        return root?[Section] as JsonObject is { } section ? (JsonObject)section.DeepClone() : new JsonObject();
    }

    public static void Save(JsonObject section)
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = new JsonObject { [Section] = section.DeepClone() }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var tempPath = path + ".tmp";
        var fileOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var writer = new StreamWriter(tempPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), fileOptions))
            writer.Write(json);
        File.Move(tempPath, path, overwrite: true);
    }

    public static void SetToken(JsonObject section, string token)
    {
        section.Remove("ApiToken");
        section.Remove("ApiTokenProtected");
        if (OperatingSystem.IsWindows())
        {
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
            section["ApiTokenProtected"] = Convert.ToBase64String(encrypted);
        }
        else
        {
            section["ApiToken"] = token; // file is owner read/write only
        }
    }

    public static string? Unprotect(string? protectedToken)
    {
        if (string.IsNullOrEmpty(protectedToken) || !OperatingSystem.IsWindows())
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedToken), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null; // e.g. the file was copied from another user or machine
        }
    }

    public static string Mask(string? token) =>
        string.IsNullOrEmpty(token) ? "(not set)" : token.Length <= 12 ? "****" : token[..12] + "…";
}
