namespace Server.Jobs;

public class JobStorageOptions
{
    public const string SectionName = "JobStorage";

    public string RootPath { get; set; } = "App_Data/Jobs";

    public string SourceDirectoryName { get; set; } = "Source";

    public long MaxUploadBytes { get; set; } = 20L * 1024 * 1024 * 1024;

    public long MaxConfigBytes { get; set; } = 1024 * 1024;

    public string ResultFramesDirectoryName { get; set; } = "Result_Frames";

    public long MaxResultUploadBytes { get; set; } = 20L * 1024 * 1024 * 1024;

    public long MaxResultFrameBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}
