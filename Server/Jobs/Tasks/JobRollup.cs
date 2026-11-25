using Server.Data.Entities;

namespace Server.Jobs.Tasks;

public static class JobRollup
{
    public static void Apply(Job job, DateTime now)
    {
        // Failed tasks are requeued (see TaskProgressService), so they never fail the job.
        job.ErrorMessage = null;
        if (job.Tasks.All(t => t.Status == JobTaskStatus.Completed))
        {
            job.Status = JobStatus.Completed;
            job.RenderedAtUtc ??= now;
            return;
        }

        job.RenderedAtUtc = null;
        job.Status = job.Tasks.Any(t => t.Status != JobTaskStatus.Pending || t.FramesCompleted > 0)
            ? JobStatus.Rendering
            : JobStatus.Ready;
    }
}
