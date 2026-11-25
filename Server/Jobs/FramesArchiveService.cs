using System.IO.Compression;

namespace Server.Jobs;

public class FramesArchiveService(JobPaths paths, ArchiveCache cache)
{
    public string ArchivePath(Guid jobId) => Path.Combine(paths.CacheDirectory(jobId), "frames.zip");

    public Task<string> GetOrCreateAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var framesDirectory = paths.ResultFramesDirectory(jobId);
        if (!Directory.Exists(framesDirectory))
            throw new DirectoryNotFoundException($"Result_Frames directory for job {jobId} is missing.");

        // PNGs are already compressed; storing them keeps the build I/O-bound and fast.
        return cache.GetOrBuildAsync(ArchivePath(jobId), (tempPath, ct) =>
            ZipFile.CreateFromDirectoryAsync(framesDirectory, tempPath, CompressionLevel.NoCompression,
                includeBaseDirectory: false, ct), cancellationToken);
    }
}
