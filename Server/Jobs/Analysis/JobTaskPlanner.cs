namespace Server.Jobs.Analysis;

public record PlannedTask(int TaskIndex, int FrameStart, int FrameEnd, int FrameCount);

public static class JobTaskPlanner
{
    public const int DefaultSubTaskCount = 10;

    public const int MaxSubTaskCount = 1000;

    public static IReadOnlyList<PlannedTask> Plan(FrameRange range, int taskCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(taskCount, 1);

        var totalFrames = range.FrameCount;
        if (totalFrames == 0)
            return [];

        var tasks = Math.Min(taskCount, totalFrames);
        var baseSize = totalFrames / tasks;
        var remainder = totalFrames % tasks;

        var plan = new List<PlannedTask>(tasks);
        var frameOffset = 0;
        for (var i = 0; i < tasks; i++)
        {
            var count = baseSize + (i < remainder ? 1 : 0);
            var start = range.Start + frameOffset;
            var end = start + count - 1;
            plan.Add(new PlannedTask(i, start, end, count));
            frameOffset += count;
        }
        return plan;
    }
}
