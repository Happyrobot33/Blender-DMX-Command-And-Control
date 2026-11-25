using System.Collections.Concurrent;

namespace Server.Jobs;

public class ArchiveCache(ILogger<ArchiveCache> logger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<string> GetOrBuildAsync(string path, Func<string, CancellationToken, Task> build, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
            return path;

        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path))
                return path; // built by another request while we waited

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                // Not tied to the caller's cancellation: other requests may be waiting on this build.
                await build(tempPath, CancellationToken.None);
                File.Move(tempPath, path); // readers never see a partial file
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }

            logger.LogInformation("Built {Path} ({Bytes} bytes)", path, new FileInfo(path).Length);
            return path;
        }
        finally
        {
            gate.Release();
        }
    }
}
