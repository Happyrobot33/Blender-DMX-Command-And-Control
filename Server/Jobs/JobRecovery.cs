using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Data.Entities;

namespace Server.Jobs;

public static class JobRecovery
{
    public static async Task FailInterruptedJobsAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var paths = scope.ServiceProvider.GetRequiredService<JobPaths>();

        var interrupted = await db.Jobs
            .Where(j => j.Status == JobStatus.Uploading) // Uploaded/Analyzing are requeued by JobAnalysisWorker
            .Select(j => j.Id)
            .ToListAsync();
        if (interrupted.Count == 0)
            return;

        await db.Jobs.Where(j => interrupted.Contains(j.Id)).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, JobStatus.Failed)
            .SetProperty(j => j.ErrorMessage, "The server restarted before this upload finished.")
            .SetProperty(j => j.FileCount, 0)
            .SetProperty(j => j.TotalBytes, 0L));

        foreach (var jobId in interrupted)
        {
            try
            {
                paths.DeleteJobDirectory(jobId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                app.Logger.LogError(ex, "Could not remove files for interrupted job {JobId}", jobId);
            }
        }

        app.Logger.LogWarning("Marked {Count} interrupted job(s) as Failed", interrupted.Count);
    }
}
