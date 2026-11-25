using System.IO.Compression;
using Client.Rendering;
using Microsoft.Extensions.Options;

namespace Client.Worker;

public class TaskRunner(ServerApi api, IOptions<ClientOptions> options, ILogger<TaskRunner> logger)
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2);

    public async Task RunAsync(ClaimResponse claim, CachedJob job, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var task = claim.Task;
        logger.LogInformation("{Action} task {TaskId} (job {JobName} #{TaskIndex}): frames {Start}-{End}; config {Config}",
            claim.Resumed ? "Resuming" : "Starting", task.Id, claim.Job.Name, task.TaskIndex, task.FrameStart, task.FrameEnd,
            job.ConfigPath is null ? "none" : Path.GetFileName(job.ConfigPath));

        // HNode saves the frames next to the .blend, in "<blend name>_frames" (DMX Connector convention).
        var blendPath = Path.GetFullPath(Path.Combine(job.SourceDirectory, claim.Job.MainBlendFile));
        var exportDirectory = Path.Combine(Path.GetDirectoryName(blendPath)!, Path.GetFileNameWithoutExtension(blendPath) + "_frames");
        var framePaths = Enumerable.Range(task.FrameStart, task.FrameCount)
            .Select(frame => Path.Combine(exportDirectory, $"frame_{frame:D6}.png"))
            .ToList();
        // Never upload frames left over from an earlier attempt at this task.
        foreach (var path in framePaths.Where(File.Exists))
            File.Delete(path);

        RenderSession? session = null;
        try
        {
            string? failure = null;
            try
            {
                session = await RenderSession.StartAsync(
                    ApplicationPaths.Resolve(settings.HNodePath, ApplicationPaths.DefaultHNode)
                        ?? throw new RenderSessionException("HNode not found."),
                    ApplicationPaths.Resolve(settings.BlenderPath, ApplicationPaths.DefaultBlender)
                        ?? throw new RenderSessionException("Blender not found."),
                    job, claim.Job.MainBlendFile, task.Id, task.FrameStart, task.FrameEnd,
                    logger, cancellationToken);

                using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var progress = ReportProgressAsync(task.Id, framePaths, progressCts.Token);
                try
                {
                    await session.WaitForExportAsync(cancellationToken);
                }
                finally
                {
                    await progressCts.CancelAsync();
                    await progress;
                }

                var missing = framePaths.Where(path => !File.Exists(path)).ToList();
                if (missing.Count > 0)
                    throw new RenderSessionException(
                        $"The frame export finished but {missing.Count} of {framePaths.Count} frames are missing (first: {Path.GetFileName(missing[0])}).");

                await UploadAsync(task.Id, framePaths, job, cancellationToken);
                logger.LogInformation("Task {TaskId} completed: {Frames} frames uploaded", task.Id, framePaths.Count);
            }
            catch (RenderSessionException ex)
            {
                failure = ex.Message;
            }
            catch (ServerRejectedException ex) when (ex.StatusCode == 400)
            {
                failure = $"The server rejected the frames: {ex.Message}";
            }

            if (failure is not null)
            {
                logger.LogError("Task {TaskId} failed: {Message}", task.Id, failure);
                await api.ReportProgressAsync(task.Id, CountFrames(framePaths), "Failed", failure, cancellationToken);
            }
        }
        finally
        {
            // Cleanup: close Blender (if it hasn't quit itself) and HNode once the task has been processed.
            if (session is not null)
                await session.DisposeAsync();
        }
    }

    private async Task ReportProgressAsync(long taskId, IReadOnlyList<string> framePaths, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ProgressInterval);
        var lastReported = -1;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var done = CountFrames(framePaths);
                if (done == lastReported)
                    continue;
                try
                {
                    await api.ReportProgressAsync(taskId, done, status: null, error: null, cancellationToken);
                    lastReported = done;
                    logger.LogInformation("Task {TaskId}: {Done}/{Total} frames exported", taskId, done, framePaths.Count);
                }
                catch (Exception ex) when (ex is ServerRejectedException or HttpRequestException)
                {
                    logger.LogWarning("Task {TaskId}: progress report failed ({Message})", taskId, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task UploadAsync(long taskId, IReadOnlyList<string> framePaths, CachedJob job, CancellationToken cancellationToken)
    {
        var zipPath = Path.Combine(job.Directory, "logs", $"task-{taskId}-frames.zip");
        File.Delete(zipPath);
        try
        {
            // PNGs are already compressed; store them as-is.
            await using (var zipFile = File.Create(zipPath))
            await using (var archive = await ZipArchive.CreateAsync(zipFile, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: null, cancellationToken))
            {
                foreach (var path in framePaths)
                    await archive.CreateEntryFromFileAsync(path, Path.GetFileName(path), CompressionLevel.NoCompression, cancellationToken);
            }
            logger.LogInformation("Task {TaskId}: uploading {Frames} frames ({Bytes:N0} bytes)", taskId, framePaths.Count, new FileInfo(zipPath).Length);
            await api.CompleteAsync(taskId, zipPath, cancellationToken);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    private static int CountFrames(IEnumerable<string> framePaths) => framePaths.Count(File.Exists);
}
