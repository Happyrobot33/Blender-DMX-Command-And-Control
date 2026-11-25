namespace Server.Jobs.Video;

public class VideoOptions
{
    public const string SectionName = "Video";

    public string? FfmpegPath { get; set; }

    public string[] CodecArguments { get; set; } = ["-c:v", "ffv1", "-level", "3", "-g", "1", "-slicecrc", "1"];

    public string FileExtension { get; set; } = "mkv";

    public string ResolveFfmpegPath() => string.IsNullOrWhiteSpace(FfmpegPath) ? "ffmpeg" : FfmpegPath;
}
