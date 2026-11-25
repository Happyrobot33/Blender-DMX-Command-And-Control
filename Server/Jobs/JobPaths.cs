using Microsoft.Extensions.Options;

namespace Server.Jobs;

public class JobPaths(IOptions<JobStorageOptions> options, IWebHostEnvironment environment)
{
    private static readonly char[] Separators = ['/', '\\'];

    public string Root { get; } = Path.GetFullPath(options.Value.RootPath, environment.ContentRootPath);

    public string JobDirectory(Guid jobId) => Path.Combine(Root, jobId.ToString());

    public string ConfigDirectory(Guid jobId) => Path.Combine(JobDirectory(jobId), "Config");

    public string CacheDirectory(Guid jobId) => Path.Combine(JobDirectory(jobId), ".cache");

    public string VideoDirectory(Guid jobId) => Path.Combine(JobDirectory(jobId), "Video");

    public string ResultFramesDirectory(Guid jobId) =>
        Path.Combine(JobDirectory(jobId), options.Value.ResultFramesDirectoryName);

    public string IncomingDirectory(Guid jobId) => Path.Combine(JobDirectory(jobId), ".incoming");

    public int CountResultFrames(Guid jobId)
    {
        var directory = ResultFramesDirectory(jobId);
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.png").Count() : 0;
    }

    public string SourceDirectory(Guid jobId) =>
        Path.Combine(JobDirectory(jobId), options.Value.SourceDirectoryName);

    public static string? NormalizeRelativePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || Path.IsPathRooted(rawPath))
            return null;

        var segments = rawPath.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.Contains(':'))
                return null;
        }

        return string.Join('/', segments);
    }

    public void DeleteJobDirectory(Guid jobId)
    {
        var jobDirectory = JobDirectory(jobId);
        if (Directory.Exists(jobDirectory))
            Directory.Delete(jobDirectory, recursive: true);
    }

    public IReadOnlyList<JobSourceFile> ListSourceFiles(Guid jobId)
    {
        var sourceDirectory = new DirectoryInfo(SourceDirectory(jobId));
        if (!sourceDirectory.Exists)
            return [];

        return sourceDirectory
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Select(file => new JobSourceFile(
                Path.GetRelativePath(sourceDirectory.FullName, file.FullName).Replace(Path.DirectorySeparatorChar, '/'),
                file.Length,
                file.LastWriteTimeUtc))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsBlendFile(string path) => path.EndsWith(".blend", StringComparison.OrdinalIgnoreCase);

    public static string ResolveUnder(string baseDirectory, string normalizedRelativePath)
    {
        var baseFull = Path.GetFullPath(baseDirectory);
        var candidate = Path.GetFullPath(Path.Combine(baseFull, normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!candidate.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Path '{normalizedRelativePath}' escapes the job directory.");

        return candidate;
    }
}
