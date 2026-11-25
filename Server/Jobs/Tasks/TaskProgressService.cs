using System.Data;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Data.Entities;
using Server.Jobs.Video;

namespace Server.Jobs.Tasks;

public record TaskProgressUpdate(int FramesCompleted, JobTaskStatus? Status = null, string? Error = null);

public enum TaskProgressError
{
    Invalid,

    NotFound,

    Conflict,
}

public class TaskProgressException(string message, TaskProgressError error = TaskProgressError.Invalid) : Exception(message)
{
    public TaskProgressError Error { get; } = error;
}

public class TaskProgressService(AppDbContext db, VideoEncodingQueue videoQueue, ILogger<TaskProgressService> logger)
{
    public Task<Job> UpdateAsync(long taskId, TaskProgressUpdate update, int renderMachineId, CancellationToken cancellationToken)
    {
        if (update.Status == JobTaskStatus.Completed)
            throw new TaskProgressException("Tasks are completed by uploading their frames to POST /api/tasks/{id}/complete.");
        return ApplyAsync(taskId, update, renderMachineId, cancellationToken);
    }

    public Task<Job> MarkCompletedAsync(long taskId, int frameCount, int renderMachineId, CancellationToken cancellationToken) =>
        ApplyAsync(taskId, new TaskProgressUpdate(frameCount, JobTaskStatus.Completed), renderMachineId, cancellationToken);

    private async Task<Job> ApplyAsync(long taskId, TaskProgressUpdate update, int renderMachineId, CancellationToken cancellationToken)
    {
        // A task never moves between jobs, so this lookup can happen outside the transaction.
        var jobId = await db.JobTasks.Where(t => t.Id == taskId).Select(t => (Guid?)t.JobId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new TaskProgressException($"Task {taskId} not found.", TaskProgressError.NotFound);

        // Concurrent reports for different tasks of the same job must serialize, and the job status
        // must be computed from every task update committed before ours. So: lock the job row first,
        // and use READ COMMITTED - under MariaDB's default REPEATABLE READ, reads after the lock could
        // still see a snapshot that predates other reports' commits and roll up a stale status.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM jobs WHERE id = {jobId.ToString()} FOR UPDATE", cancellationToken);

        var job = await db.Jobs.Include(j => j.Tasks).SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken)
            ?? throw new TaskProgressException($"Task {taskId} not found.", TaskProgressError.NotFound); // job deleted meanwhile
        if (job.IsInProgress)
            throw new TaskProgressException($"Job is still {job.Status.ToString().ToLowerInvariant()}.", TaskProgressError.Conflict);

        var task = job.Tasks.Single(t => t.Id == taskId);
        // Only the machine that claimed the task (POST /api/tasks/claim) may report on it.
        if (task.RenderMachineId != renderMachineId)
            throw new TaskProgressException(task.RenderMachineId is null
                ? "Task has not been claimed. Claim it with POST /api/tasks/claim first."
                : "Task is assigned to a different render machine.", TaskProgressError.Conflict);
        if (task.Status == JobTaskStatus.Completed)
            throw new TaskProgressException("Task is already completed.", TaskProgressError.Conflict);

        Apply(task, update, DateTime.UtcNow);
        JobRollup.Apply(job, DateTime.UtcNow);

        // The last task just completed: queue the job for video generation (after commit).
        var queueVideo = job.Status == JobStatus.Completed && job.VideoStatus == VideoStatus.None;
        if (queueVideo)
            job.VideoStatus = VideoStatus.Pending;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex))
        {
            throw new TaskProgressException("This machine already has an active task.", TaskProgressError.Conflict);
        }
        await transaction.CommitAsync(cancellationToken);
        if (queueVideo)
            videoQueue.Enqueue(job.Id);

        if (update.Status == JobTaskStatus.Failed)
            logger.LogWarning("Task {TaskId} (job {JobId} #{TaskIndex}) failed on machine {MachineId} (failure {FailCount}): {Error}; requeued",
                task.Id, job.Id, task.TaskIndex, renderMachineId, task.FailCount, task.ErrorMessage);
        else
            logger.LogInformation("Task {TaskId} (job {JobId} #{TaskIndex}): {Done}/{Total} frames, {TaskStatus}; job {JobStatus} {Percent:0.#}%",
                task.Id, job.Id, task.TaskIndex, task.FramesCompleted, task.FrameCount, task.Status, job.Status, job.ProgressPercent);
        return job;
    }

    private static void Apply(JobTask task, TaskProgressUpdate update, DateTime now)
    {
        if (update.FramesCompleted < 0 || update.FramesCompleted > task.FrameCount)
            throw new TaskProgressException($"framesCompleted must be between 0 and {task.FrameCount}.");

        var status = update.Status ?? JobTaskStatus.Running;

        switch (status)
        {
            case JobTaskStatus.Pending or JobTaskStatus.Assigned:
                throw new TaskProgressException("status must be Running or Failed.");
            case JobTaskStatus.Completed when update.FramesCompleted != task.FrameCount:
                throw new TaskProgressException($"A Completed task must report all {task.FrameCount} frames.");
            case JobTaskStatus.Failed when string.IsNullOrWhiteSpace(update.Error):
                throw new TaskProgressException("error is required when status is Failed.");
        }

        if (status == JobTaskStatus.Failed)
        {
            // Don't fail the task (or the job): count the failure, keep the reason, and put the task
            // back in the queue so it can be claimed again (by any machine).
            task.FailCount++;
            task.LastFailedAtUtc = now;
            task.ErrorMessage = Truncate(update.Error!.Trim(), 2000);
            task.Status = JobTaskStatus.Pending;
            task.RenderMachineId = null;
            task.FramesCompleted = 0;
            task.ClaimedAtUtc = null;
            task.StartedAtUtc = null;
            task.CompletedAtUtc = null;
            task.UpdatedAtUtc = now;
            return;
        }

        task.FramesCompleted = update.FramesCompleted;
        task.Status = status;
        task.StartedAtUtc ??= now;
        task.CompletedAtUtc = status == JobTaskStatus.Completed ? task.CompletedAtUtc ?? now : null;
        task.UpdatedAtUtc = now;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
