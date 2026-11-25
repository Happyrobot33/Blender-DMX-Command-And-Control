using System.Data;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Data.Entities;

namespace Server.Jobs.Tasks;

public record ClaimResult(JobTask Task, bool Resumed);

public class TaskClaimService(AppDbContext db, ILogger<TaskClaimService> logger)
{
    private const int CandidateBatchSize = 20;

    // Only Ready/Rendering jobs hand out tasks. Written as explicit comparisons rather than
    // array.Contains(...): with C# 14 that binds to a span overload EF Core 9 can't translate.

    public async Task<ClaimResult?> ClaimNextAsync(int renderMachineId, CancellationToken cancellationToken)
    {
        var current = await FindActiveTaskAsync(renderMachineId, cancellationToken);
        if (current is not null)
            return new ClaimResult(current, Resumed: true);

        while (true)
        {
            var candidates = await db.JobTasks.AsNoTracking()
                .Where(t => t.Status == JobTaskStatus.Pending && (t.Job.Status == JobStatus.Ready || t.Job.Status == JobStatus.Rendering))
                // Oldest job first; within a job, tasks that have failed before go to the back of the queue.
                .OrderBy(t => t.Job.CreatedAtUtc).ThenBy(t => t.FailCount).ThenBy(t => t.TaskIndex)
                .Select(t => new { t.Id, t.JobId })
                .Take(CandidateBatchSize)
                .ToListAsync(cancellationToken);

            if (candidates.Count == 0)
                return null;

            foreach (var candidate in candidates)
            {
                var result = await TryClaimAsync(candidate.Id, candidate.JobId, renderMachineId, cancellationToken);
                if (result is not null)
                    return result;
            }
            // Every candidate was taken by other machines in the meantime; look again.
        }
    }

    public Task<JobTask?> FindActiveTaskAsync(int renderMachineId, CancellationToken cancellationToken) =>
        db.JobTasks.AsNoTracking()
            .Where(t => t.RenderMachineId == renderMachineId &&
                        (t.Status == JobTaskStatus.Assigned || t.Status == JobTaskStatus.Running))
            .OrderBy(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<ClaimResult?> TryClaimAsync(long taskId, Guid jobId, int renderMachineId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // 1. Serialize this machine's claims and re-check that it still has no active task.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM render_machines WHERE id = {renderMachineId} FOR UPDATE", cancellationToken);
        var current = await FindActiveTaskAsync(renderMachineId, cancellationToken);
        if (current is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ClaimResult(current, Resumed: true);
        }

        // 2. Same protocol as progress reports: lock the job row, then read committed data,
        //    so the job's rolled-up status stays consistent with its tasks.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM jobs WHERE id = {jobId.ToString()} FOR UPDATE", cancellationToken);

        var now = DateTime.UtcNow;
        int affected;
        try
        {
            affected = await db.JobTasks
                .Where(t => t.Id == taskId && t.Status == JobTaskStatus.Pending && (t.Job.Status == JobStatus.Ready || t.Job.Status == JobStatus.Rendering))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, JobTaskStatus.Assigned)
                    .SetProperty(t => t.RenderMachineId, renderMachineId)
                    .SetProperty(t => t.ClaimedAtUtc, now)
                    .SetProperty(t => t.UpdatedAtUtc, now), cancellationToken);
        }
        catch (Exception ex) when (DbErrors.IsDuplicateKey(ex))
        {
            // ux_job_tasks_active_machine: the machine already holds a task (backstop for the lock above).
            await transaction.RollbackAsync(cancellationToken);
            var existing = await FindActiveTaskAsync(renderMachineId, cancellationToken);
            return existing is null ? null : new ClaimResult(existing, Resumed: true);
        }

        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null; // another machine got it first (or the job stopped being claimable)
        }

        var job = await db.Jobs.Include(j => j.Tasks).SingleAsync(j => j.Id == jobId, cancellationToken);
        JobRollup.Apply(job, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var task = job.Tasks.Single(t => t.Id == taskId);
        logger.LogInformation("Machine {MachineId} claimed task {TaskId} (job {JobId} #{TaskIndex}, frames {Start}-{End})",
            renderMachineId, task.Id, job.Id, task.TaskIndex, task.FrameStart, task.FrameEnd);
        return new ClaimResult(task, Resumed: false);
    }

    public async Task<int> ReleaseMachineTasksAsync(int renderMachineId, CancellationToken cancellationToken)
    {
        var jobIds = await db.JobTasks
            .Where(t => t.RenderMachineId == renderMachineId &&
                        (t.Status == JobTaskStatus.Assigned || t.Status == JobTaskStatus.Running))
            .Select(t => t.JobId).Distinct()
            .ToListAsync(cancellationToken);

        var released = 0;
        foreach (var jobId in jobIds)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM jobs WHERE id = {jobId.ToString()} FOR UPDATE", cancellationToken);

            var job = await db.Jobs.Include(j => j.Tasks).SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken);
            if (job is null)
                continue;

            var now = DateTime.UtcNow;
            foreach (var task in job.Tasks.Where(t => t.RenderMachineId == renderMachineId &&
                                                      t.Status is JobTaskStatus.Assigned or JobTaskStatus.Running))
            {
                task.Status = JobTaskStatus.Pending;
                task.RenderMachineId = null;
                task.FramesCompleted = 0;
                task.ClaimedAtUtc = null;
                task.StartedAtUtc = null;
                task.UpdatedAtUtc = now;
                released++;
            }
            JobRollup.Apply(job, now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return released;
    }
}
