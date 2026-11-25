namespace Client.Rendering;

public static class ApplicationPaths
{
    public static string DefaultHNode() =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HNode", "HNode.exe")
        : OperatingSystem.IsMacOS() ? "/Applications/HNode.app/Contents/MacOS/HNode"
        : "HNode";

    public static string DefaultBlender()
    {
        if (OperatingSystem.IsWindows())
        {
            var foundation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
            var newest = Directory.Exists(foundation)
                ? Directory.EnumerateDirectories(foundation, "Blender*")
                    .Select(dir => Path.Combine(dir, "blender.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(path => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(path))!.Replace("Blender", "").Trim(), out var v) ? v : new Version(0, 0))
                    .FirstOrDefault()
                : null;
            return newest ?? "blender.exe";
        }
        return OperatingSystem.IsMacOS() ? "/Applications/Blender.app/Contents/MacOS/Blender" : "blender";
    }

    public static string? Resolve(string? configured, Func<string> fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(configured) ? fallback() : configured.Trim().Trim('"');
        if (candidate.Contains(Path.DirectorySeparatorChar) || candidate.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;

        var extensions = OperatingSystem.IsWindows() && !Path.HasExtension(candidate) ? new[] { ".exe", "" } : [""];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var full = Path.Combine(directory, candidate + extension);
                if (File.Exists(full))
                    return full;
            }
        }
        return null;
    }
}
