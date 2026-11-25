using Client.Configuration;
using Client.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

// Render machine client: claims tasks from the server, downloads and caches each job's files, and
// processes tasks by starting HNode (with the job's show config) and Blender (with its .blend).
//
//   Client configure        set the server URL and API token (saved per user)
//   Client config show      print the settings in effect
//   Client                  run the render machine
//
// Settings, lowest to highest precedence: appsettings.json, the per-user settings file written by
// `configure`, Emulator__* environment variables, --Emulator:* command-line arguments.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory, // find appsettings.json regardless of the working directory
});

// Insert the per-user settings file just above appsettings*.json (below environment variables).
// The folder is created first: a JSON source whose folder doesn't exist can't find the file later.
JsonConfigurationSource UserSettingsSource()
{
    Directory.CreateDirectory(Path.GetDirectoryName(UserSettings.FilePath)!);
    var source = new JsonConfigurationSource { Path = UserSettings.FilePath, Optional = true, ReloadOnChange = false };
    source.ResolveFileProvider();
    return source;
}

var sources = builder.Configuration.Sources;
var userSettings = UserSettingsSource();
// The host also adds a DOTNET_-prefixed environment source *before* appsettings.json, so look for
// the unprefixed one that comes after it.
var envIndex = sources.ToList().FindIndex(s => s is EnvironmentVariablesConfigurationSource { Prefix: null or "" });
sources.Insert(envIndex < 0 ? sources.Count : envIndex, userSettings);

builder.Services.Configure<ClientOptions>(builder.Configuration.GetSection(ClientOptions.SectionName));
builder.Services.PostConfigure<ClientOptions>(ResolveToken);

static void ResolveToken(ClientOptions options)
{
    if (string.IsNullOrWhiteSpace(options.ApiToken))
        options.ApiToken = UserSettings.Unprotect(options.ApiTokenProtected) ?? string.Empty;
}

ClientOptions CurrentOptions()
{
    var options = new ClientOptions();
    builder.Configuration.GetSection(ClientOptions.SectionName).Bind(options);
    ResolveToken(options);
    return options;
}

if (ConfigureCommand.IsShow(args))
{
    ConfigureCommand.Show(CurrentOptions());
    return 0;
}

if (ConfigureCommand.IsConfigure(args))
    return await ConfigureCommand.RunAsync(CurrentOptions());

// First run without a token: set up interactively, or explain how when there's no terminal.
if (string.IsNullOrWhiteSpace(CurrentOptions().ApiToken))
{
    if (Console.IsInputRedirected)
    {
        Console.Error.WriteLine("No API token configured. Run 'Client configure' in a terminal, or set Emulator__ApiToken.");
        return 2;
    }
    Console.WriteLine("No API token configured yet - starting setup.\n");
    await ConfigureCommand.RunAsync(CurrentOptions());
    Console.WriteLine();

    // Reload so the run below uses what was just saved.
    var index = sources.IndexOf(userSettings);
    sources.RemoveAt(index);
    sources.Insert(index, UserSettingsSource());
}

builder.Services.AddSingleton<ServerApi>();
builder.Services.AddSingleton<JobCache>();
builder.Services.AddSingleton<TaskRunner>();
builder.Services.AddHostedService<RenderWorker>();

await builder.Build().RunAsync();
return 0;
