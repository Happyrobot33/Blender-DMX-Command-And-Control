namespace Client.Worker;

// Shapes of the server's /api responses (System.Text.Json web defaults: camelCase).

public record ClaimResponse(bool Resumed, ClaimedTask Task, ClaimedJob Job);

public record ClaimedTask(
    long Id, int TaskIndex, string Status, int FramesCompleted,
    int FrameStart, int FrameEnd, int FrameCount);

public record ClaimedJob(
    Guid Id, string Name, string? SceneName, string MainBlendFile,
    string SourceUrl, string? ConfigFileName, string? ConfigUrl);

public class ServerRejectedException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
