using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Server.Data;
using Server.Data.Entities;
using Server.Jobs.Analysis;
using Server.Jobs.Tasks;

namespace Server.Jobs.Video;

public partial class VideoEncodingWorker(
    VideoEncodingQueue queue,
    IServiceScopeFactory scopeFactory,
    FfmpegEncoder encoder,
    BlendFileScanner scanner,
    FramesArchiveService framesArchive,
    JobPaths paths,
    IOptions<VideoOptions> options,
    ILogger<VideoEncodingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex UnsafeFileNameChars();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueUnfinishedAsync(stoppingToken);

        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await EncodeJobAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // left Encoding; reset to Pending and requeued on next start
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error encoding video for job {JobId}", jobId);
                await SetFailedAsync(jobId, "Video generation failed on the server.");
            }
        }
    }

    private async Task RequeueUnfinishedAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Jobs.Where(j => j.VideoStatus == VideoStatus.Encoding || j.VideoStatus == VideoStatus.Packaging)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.VideoStatus, VideoStatus.Pending), cancellationToken);
        var pending = await db.Jobs.Where(j => j.VideoStatus == VideoStatus.Pending)
            .OrderBy(j => j.RenderedAtUtc).Select(j => j.Id).ToListAsync(cancellationToken);
        foreach (var id in pending)
            queue.Enqueue(id);
        if (pending.Count > 0)
            logger.LogInformation("Requeued {Count} job(s) for video generation", pending.Count);
    }

    private async Task EncodeJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = await db.Jobs.Include(j => j.Tasks).SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null || job.VideoStatus != VideoStatus.Pending)
            return;
        if (job.Status != JobStatus.Completed)
        {
            await SetFailedAsync(jobId, "The job's tasks are not all completed.");
            return;
        }

        // Jobs analyzed before frame rates were recorded: read it from the .blend now.
        if (job.FrameRate is null)
        {
            try
            {
                var range = await scanner.ScanFrameRangeAsync(
                    JobPaths.ResolveUnder(paths.SourceDirectory(job.Id), job.MainBlendFile), cancellationToken);
                job.FrameRate = range.FrameRate;
            }
            catch (BlendScanException ex)
            {
                await SetFailedAsync(jobId, $"Could not read the frame rate from the .blend file: {ex.Message}");
                return;
            }
        }

        // Every frame of every task, in order, must be present.
        var resultDirectory = paths.ResultFramesDirectory(job.Id);
        var frames = job.Tasks.OrderBy(t => t.TaskIndex)
            .SelectMany(t => Enumerable.Range(t.FrameStart, t.FrameCount))
            .Select(frame => Path.Combine(resultDirectory, TaskResultService.FrameFileName(frame)))
            .ToList();
        var missing = frames.Where(f => !File.Exists(f)).ToList();
        if (missing.Count > 0)
        {
            await SetFailedAsync(jobId, $"{missing.Count} rendered frame(s) are missing from Result_Frames (e.g. {Path.GetFileName(missing[0])}).");
            return;
        }

        var frameRate = job.FrameRate!.Value;

        var fileName = $"{SafeFileName(job.Name)}.{options.Value.FileExtension}";
        var videoDirectory = paths.VideoDirectory(job.Id);
        Directory.CreateDirectory(videoDirectory);
        var tempPath = Path.Combine(videoDirectory, $".{Guid.NewGuid():N}.tmp");

        job.VideoStatus = VideoStatus.Encoding;
        job.VideoFramesEncoded = 0;
        job.VideoFrameCount = frames.Count;
        job.VideoError = null;
        job.VideoStartedAtUtc = DateTime.UtcNow;
        job.VideoCompletedAtUtc = null;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Encoding video for job {JobId}: {Frames} frames at {Fps} fps -> {File}",
            job.Id, frames.Count, frameRate, fileName);

        var lastReport = DateTime.MinValue;
        try
        {
            await encoder.EncodeAsync(frames, frameRate, tempPath, async encoded =>
            {
                if (DateTime.UtcNow - lastReport < ProgressInterval)
                    return;
                lastReport = DateTime.UtcNow;
                await db.Jobs.Where(j => j.Id == jobId)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.VideoFramesEncoded, Math.Min(encoded, frames.Count)), CancellationToken.None);
            }, cancellationToken);

            // Replace any previous video (e.g. a regeneration) with the new one.
            foreach (var old in Directory.EnumerateFiles(videoDirectory).Where(f => !f.Equals(tempPath)))
                File.Delete(old);
            File.Move(tempPath, Path.Combine(videoDirectory, fileName));

            // Build the frames zip now so its download is instant (no-op if it already exists -
            // frames can't change once the job is complete, e.g. when regenerating the video).
            await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.VideoStatus, VideoStatus.Packaging)
                .SetProperty(j => j.VideoFramesEncoded, frames.Count)
                .SetProperty(j => j.VideoFileName, fileName), CancellationToken.None);
            await framesArchive.GetOrCreateAsync(jobId, cancellationToken);

            await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.VideoStatus, VideoStatus.Ready)
                .SetProperty(j => j.VideoCompletedAtUtc, DateTime.UtcNow), CancellationToken.None);
            logger.LogInformation("Video ready for job {JobId}: {File}", jobId, fileName);
        }
        catch (VideoEncodingException ex)
        {
            await SetFailedAsync(jobId, ex.Message);
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "File error during video generation for job {JobId}", jobId);
            await SetFailedAsync(jobId, $"File error: {ex.Message}");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private async Task SetFailedAsync(Guid jobId, string message)
    {
        logger.LogWarning("Video generation for job {JobId} failed: {Message}", jobId, message);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var error = message.Length <= 2000 ? message : message[..2000];
        await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.VideoStatus, VideoStatus.Failed)
            .SetProperty(j => j.VideoError, error)
            .SetProperty(j => j.VideoCompletedAtUtc, DateTime.UtcNow), CancellationToken.None);
    }

    private static string SafeFileName(string name)
    {
        var safe = UnsafeFileNameChars().Replace(name, "_").Trim('_', '.');
        if (safe.Length > 100)
            safe = safe[..100];
        return safe.Length == 0 ? "video" : safe;
    }
}
