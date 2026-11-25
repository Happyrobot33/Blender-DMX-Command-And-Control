using Humanizer;

namespace Server.Jobs;

public static class RemainingTime
{
    public static TimeSpan? Estimate(int done, int total, DateTime? startedAtUtc, DateTime nowUtc)
    {
        if (startedAtUtc is not DateTime started || done <= 0 || done >= total)
            return null;

        // Timestamps are stored in UTC but come back from the database without a Kind.
        var elapsed = nowUtc - DateTime.SpecifyKind(started, DateTimeKind.Utc);
        if (elapsed <= TimeSpan.Zero)
            return null;

        return elapsed / done * (total - done);
    }

    public static string Describe(TimeSpan remaining) =>
        remaining < TimeSpan.FromSeconds(1)
            ? "less than a second left"
            : $"about {remaining.Humanize(precision: 2, minUnit: TimeUnit.Second, maxUnit: TimeUnit.Day)} left";
}
