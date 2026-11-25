using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Client.Worker;

public record CachedJob(Guid Id, string Directory, string SourceDirectory, string? ConfigPath);

public class JobCache(ServerApi api, IOptions<ClientOptions> options, ILogger<JobCache> logger)
{
    private const string CompleteMarker = ".complete";

    public async Task<CachedJob> EnsureAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        var jobDirectory = Path.Combine(options.Value.ResolveCacheDirectory(), job.Id.ToString());
        var sourceDirectory = Path.Combine(jobDirectory, "Source");
        var configPath = job.ConfigFileName is null ? null : Path.Combine(jobDirectory, "Config", Path.GetFileName(job.ConfigFileName));
        var cached = new CachedJob(job.Id, jobDirectory, sourceDirectory, configPath);

        if (File.Exists(Path.Combine(jobDirectory, CompleteMarker)))
        {
            logger.LogInformation("Job {JobId} ({JobName}) is cached", job.Id, job.Name);
            return cached;
        }

        Directory.CreateDirectory(jobDirectory);
        await File.WriteAllTextAsync(Path.Combine(jobDirectory, "job.json"),
            JsonSerializer.Serialize(job, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

        logger.LogInformation("Downloading source for job {JobId} ({JobName})", job.Id, job.Name);
        var zipPath = Path.Combine(jobDirectory, "source.zip");
        await api.DownloadAsync(job.SourceUrl, zipPath, cancellationToken);
        if (Directory.Exists(sourceDirectory))
            Directory.Delete(sourceDirectory, recursive: true);
        // ExtractToDirectory refuses entries that would land outside the destination.
        ZipFile.ExtractToDirectory(zipPath, sourceDirectory);
        File.Delete(zipPath);

        if (job.ConfigUrl is not null && configPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            await api.DownloadAsync(job.ConfigUrl, configPath, cancellationToken);
            logger.LogInformation("Downloaded HNode config {Config}", Path.GetFileName(configPath));
        }

        await File.WriteAllTextAsync(Path.Combine(jobDirectory, CompleteMarker), DateTime.UtcNow.ToString("O"), cancellationToken);
        var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).Count();
        logger.LogInformation("Cached job {JobId}: {Files} source file(s) in {Directory}", job.Id, files, jobDirectory);
        return cached;
    }
}
