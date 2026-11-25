using System.IO.Compression;

namespace Server.Jobs;

public class SourceArchiveService(JobPaths paths, ArchiveCache cache)
{
    public string ArchivePath(Guid jobId) => Path.Combine(paths.CacheDirectory(jobId), "source.zip");

    public Task<string> GetOrCreateAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var sourceDirectory = paths.SourceDirectory(jobId);
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Source directory for job {jobId} is missing.");

        // Stored uncompressed - textures, caches and .blend files rarely shrink much, and this keeps
        // builds fast. Entry names are relative to Source ("textures/wood.png").
        return cache.GetOrBuildAsync(ArchivePath(jobId), (tempPath, ct) =>
            ZipFile.CreateFromDirectoryAsync(sourceDirectory, tempPath, CompressionLevel.NoCompression,
                includeBaseDirectory: false, ct), cancellationToken);
    }
}
