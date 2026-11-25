using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Server.Jobs.Analysis;

public class BlendScanException(string message) : Exception(message);

public class BlendFileScanner(IOptions<BlenderOptions> options, ILogger<BlendFileScanner> logger)
{
    private const string ResultMarker = "BDMX_FRAME_RANGE=";

    // The active scene is the one `blender -b file.blend -a` renders. The frame rate is always a whole
    // number: the scene's fps (fps_base is ignored).
    private const string ScanScript =
        "import bpy, json; s = bpy.context.scene; " +
        "print('" + ResultMarker + "' + json.dumps({'scene': s.name, 'start': s.frame_start, 'end': s.frame_end, " +
        "'fps': s.render.fps}), flush=True)";

    public async Task<FrameRange> ScanFrameRangeAsync(string blendFilePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(blendFilePath))
            throw new BlendScanException($"Main .blend file not found on disk: {blendFilePath}");

        var settings = options.Value;
        var startInfo = new ProcessStartInfo(settings.ResolveExecutablePath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(blendFilePath)!,
        };
        // Uploaded files are untrusted: --disable-autoexec stops embedded Python (drivers, registered
        // text blocks) from running, and --factory-startup ignores the host's user preferences/add-ons.
        foreach (var arg in new[]
                 {
                     "--background", "--factory-startup", "--disable-autoexec", blendFilePath,
                     "--python-exit-code", "1", "--python-expr", ScanScript,
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ScanTimeoutSeconds));

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new BlendScanException($"Could not start Blender at '{startInfo.FileName}': {ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new BlendScanException($"Blender did not finish scanning within {settings.ScanTimeoutSeconds}s.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var resultLine = stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(ResultMarker, StringComparison.Ordinal));
        if (process.ExitCode != 0 || resultLine is null)
        {
            logger.LogWarning("Blender scan of {BlendFile} failed (exit {ExitCode}). stdout: {Stdout} stderr: {Stderr}",
                blendFilePath, process.ExitCode, Tail(stdout), Tail(stderr));
            throw new BlendScanException($"Blender could not read the frame range (exit code {process.ExitCode}).");
        }

        var result = JsonSerializer.Deserialize<ScanResult>(resultLine[ResultMarker.Length..])
            ?? throw new BlendScanException("Blender returned an empty scan result.");
        if (result.fps <= 0)
            throw new BlendScanException($"Scene '{result.scene}' has an invalid frame rate ({result.fps}).");
        var range = new FrameRange(result.scene, result.start, result.end, result.fps);

        if (range.FrameCount == 0)
            throw new BlendScanException($"Scene '{range.SceneName}' has an empty frame range ({range.Start}–{range.End}).");

        logger.LogInformation("Scanned {BlendFile}: scene {Scene}, frames {Start}-{End}, {Fps} fps",
            blendFilePath, range.SceneName, range.Start, range.End, range.FrameRate);
        return range;
    }

    private static string Tail(string text, int maxLength = 2000) =>
        text.Length <= maxLength ? text : text[^maxLength..];

    // ReSharper disable InconsistentNaming - matches the JSON printed by the scan script.
    private record ScanResult(string scene, int start, int end, int fps);
}
