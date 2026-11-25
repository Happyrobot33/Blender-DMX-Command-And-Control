namespace Server.Jobs.Analysis;

public class BlenderOptions
{
    public const string SectionName = "Blender";

    public string? ExecutablePath { get; set; }

    public int ScanTimeoutSeconds { get; set; } = 120;

    public string ResolveExecutablePath() =>
        !string.IsNullOrWhiteSpace(ExecutablePath) ? ExecutablePath
        : OperatingSystem.IsMacOS() ? "/Applications/Blender.app/Contents/MacOS/Blender"
        : "blender";
}
