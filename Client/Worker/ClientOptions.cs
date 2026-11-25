namespace Client.Worker;

public class ClientOptions
{
    public const string SectionName = "Emulator";

    public string ServerUrl { get; set; } = "http://localhost:5274";

    public string ApiToken { get; set; } = string.Empty;

    public string? ApiTokenProtected { get; set; }

    public string? HNodePath { get; set; }

    public string? BlenderPath { get; set; }


    public double PollIntervalSeconds { get; set; } = 5;

    public string CacheDirectory { get; set; } = "cache";

    public string ResolveCacheDirectory() => Path.GetFullPath(CacheDirectory, AppContext.BaseDirectory);
}
