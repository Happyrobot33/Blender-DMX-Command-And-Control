using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Data.Entities;

namespace Server.Jobs.Analysis;

// Background step after an upload: scans the job's main .blend for its frame range and frame rate,
// creates the sub tasks (Uploaded -> Analyzing -> Ready), or marks the job Failed with the reason.
// One job at a time; jobs left Uploaded/Analyzing by a restart are picked up again on startup.
public class JobAnalysisWorker(
    JobAnalysisQueue queue,
    IServiceScopeFactory scopeFactory,
    BlendFileScanner scanner,
    JobPaths paths,
    ILogger<JobAnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueUnfinishedAsync(stoppingToken);

        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await AnalyzeAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // left Analyzing; requeued on next start
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error analyzing job {JobId}", jobId);
                await SetFailedAsync(jobId, "Analysis failed on the server.");
            }
        }
    }

    private async Task RequeueUnfinishedAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = await db.Jobs
            .Where(j => j.Status == JobStatus.Uploaded || j.Status == JobStatus.Analyzing)
            .OrderBy(j => j.CreatedAtUtc)
            .Select(j => j.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in pending)
            queue.Enqueue(id);
        if (pending.Count > 0)
            logger.LogInformation("Requeued {Count} job(s) for analysis", pending.Count);
    }

    private async Task AnalyzeAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = await db.Jobs.Include(j => j.Tasks).SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null || job.Status is not (JobStatus.Uploaded or JobStatus.Analyzing))
            return;

        job.Status = JobStatus.Analyzing;
        await db.SaveChangesAsync(cancellationToken);

        FrameRange range;
        try
        {
            range = await scanner.ScanFrameRangeAsync(
                JobPaths.ResolveUnder(paths.SourceDirectory(job.Id), job.MainBlendFile), cancellationToken);
        }
        catch (BlendScanException ex)
        {
            await SetFailedAsync(jobId, ex.Message);
            return;
        }

        var plan = JobTaskPlanner.Plan(range, job.RequestedTaskCount);
        var now = DateTime.UtcNow;
        db.JobTasks.RemoveRange(job.Tasks); // a retry after a restart starts clean
        job.Tasks.AddRange(plan.Select(p => new JobTask
        {
            TaskIndex = p.TaskIndex,
            FrameStart = p.FrameStart,
            FrameEnd = p.FrameEnd,
            FrameCount = p.FrameCount,
            Status = JobTaskStatus.Pending,
            CreatedAtUtc = now,
        }));
        job.SceneName = range.SceneName.Length <= 255 ? range.SceneName : range.SceneName[..255];
        job.FrameStart = range.Start;
        job.FrameEnd = range.End;
        job.FrameRate = range.FrameRate;
        job.AnalyzedAtUtc = now;
        job.Status = JobStatus.Ready;
        await db.SaveChangesAsync(cancellationToken); // tasks and Ready are committed together

        logger.LogInformation("Job {JobId} ready: scene {Scene}, frames {Start}-{End} ({Frames} frames) at {Fps} fps in {Tasks} sub tasks",
            job.Id, range.SceneName, range.Start, range.End, range.FrameCount, range.FrameRate, plan.Count);
    }

    // The project files are kept so the job can be inspected; deleting the job removes them.
    private async Task SetFailedAsync(Guid jobId, string message)
    {
        logger.LogWarning("Analysis of job {JobId} failed: {Message}", jobId, message);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var error = message.Length <= 2000 ? message : message[..2000];
        await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, JobStatus.Failed)
            .SetProperty(j => j.ErrorMessage, error), CancellationToken.None);
    }
}
