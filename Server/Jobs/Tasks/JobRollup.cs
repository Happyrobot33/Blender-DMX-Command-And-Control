using Server.Data.Entities;

namespace Server.Jobs.Tasks;

public static class JobRollup
{
    public static void Apply(Job job, DateTime now)
    {
        var failed = job.Tasks.Where(t => t.Status == JobTaskStatus.Failed).OrderBy(t => t.TaskIndex).FirstOrDefault();
        if (failed is not null)
        {
            job.Status = JobStatus.Failed;
            job.ErrorMessage = Truncate($"Task {failed.TaskIndex + 1} (frames {failed.FrameStart}–{failed.FrameEnd}) failed: {failed.ErrorMessage}", 2000);
            job.RenderedAtUtc = null;
            return;
        }

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

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
