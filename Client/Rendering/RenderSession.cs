using System.Diagnostics;
using Client.Worker;

namespace Client.Rendering;

public class RenderSessionException(string message) : Exception(message);

public sealed class RenderSession : IAsyncDisposable
{
    private static readonly TimeSpan HNodeStartupTime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CloseGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BlenderSelfQuitWait = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private Process? _hnode;
    private Process? _blender;
    private StreamWriter? _blenderLog;

    private const string ArtNetAddress = "127.0.0.1";
    private const int ArtNetPort = 6454;

    private const string SetupScriptResource = "blender_task_setup.py";
    private const string ErrorPrefix = "BDMX_ERROR: ";
    private const string StatusPrefix = "BDMX: ";
    private const string ExportDoneLine = "BDMX_EXPORT_DONE";

    private readonly TaskCompletionSource _exportDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource<string> _blenderError = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private RenderSession(ILogger logger) => _logger = logger;

    public static async Task<RenderSession> StartAsync(
        string hnodePath, string blenderPath,
        CachedJob job, string mainBlendFile, long taskId, int frameStart, int frameEnd,
        ILogger logger, CancellationToken cancellationToken)
    {
        var session = new RenderSession(logger);
        try
        {
            var logDirectory = Path.Combine(job.Directory, "logs");
            Directory.CreateDirectory(logDirectory);
            var hnodeLog = Path.Combine(logDirectory, $"task-{taskId}-hnode.log");
            var blenderLog = Path.Combine(logDirectory, $"task-{taskId}-blender.log");

            // 1. HNode with the job's show configuration.
            var hnodeStart = new ProcessStartInfo(hnodePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(hnodePath)!,
            };
            if (job.ConfigPath is not null)
                hnodeStart.ArgumentList.Add($"--config-file={job.ConfigPath}");
            else
                logger.LogWarning("Job {JobId} has no HNode config; starting HNode without one", job.Id);
            hnodeStart.ArgumentList.Add("-logFile"); // Unity player option: write the log here
            hnodeStart.ArgumentList.Add(hnodeLog);

            session._hnode = Process.Start(hnodeStart) ?? throw new RenderSessionException($"Could not start HNode at '{hnodePath}'.");
            logger.LogInformation("Started HNode (pid {Pid}) with config {Config}; waiting {Seconds}s for it to start",
                session._hnode.Id, job.ConfigPath is null ? "(none)" : Path.GetFileName(job.ConfigPath), HNodeStartupTime.TotalSeconds);

            // 2. Fixed startup wait (no readiness check), failing fast if HNode exits meanwhile.
            var exited = session._hnode.WaitForExitAsync(cancellationToken);
            var waited = Task.Delay(HNodeStartupTime, cancellationToken);
            if (await Task.WhenAny(exited, waited) == exited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new RenderSessionException($"HNode exited during startup (exit code {session._hnode.ExitCode}); see {hnodeLog}");
            }
            await waited;

            // 3. Blender with the main .blend file.
            var blendPath = Path.GetFullPath(Path.Combine(job.SourceDirectory, mainBlendFile));
            if (!blendPath.StartsWith(Path.GetFullPath(job.SourceDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(blendPath))
                throw new RenderSessionException($"Main .blend file '{mainBlendFile}' is missing from the job's source files.");

            var blenderStart = new ProcessStartInfo(blenderPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(blendPath)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            blenderStart.ArgumentList.Add(blendPath);
            // Runs after the file has loaded: configure the DMX Connector and start the snapshot export.
            blenderStart.ArgumentList.Add("--python");
            blenderStart.ArgumentList.Add(await WriteSetupScriptAsync(logDirectory, cancellationToken));
            blenderStart.ArgumentList.Add("--");
            foreach (var arg in new[]
                     {
                         "--frame-start", frameStart.ToString(), "--frame-end", frameEnd.ToString(),
                         "--artnet-ip", ArtNetAddress, "--artnet-port", ArtNetPort.ToString(),
                     })
                blenderStart.ArgumentList.Add(arg);

            session._blenderLog = new StreamWriter(blenderLog, append: true) { AutoFlush = true };
            session._blender = new Process { StartInfo = blenderStart };
            session._blender.OutputDataReceived += (_, e) => session.WriteBlenderLog(e.Data);
            session._blender.ErrorDataReceived += (_, e) => session.WriteBlenderLog(e.Data);
            if (!session._blender.Start())
                throw new RenderSessionException($"Could not start Blender at '{blenderPath}'.");
            session._blender.BeginOutputReadLine();
            session._blender.BeginErrorReadLine();
            logger.LogInformation("Opened {Blend} in Blender (pid {Pid}) for frames {Start}-{End}; Art-Net -> {Address}:{Port}; logs in {Logs}",
                mainBlendFile, session._blender.Id, frameStart, frameEnd, ArtNetAddress, ArtNetPort, logDirectory);
            return session;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            await session.DisposeAsync();
            throw new RenderSessionException($"Could not start an application: {ex.Message}");
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public bool IsRunning => _hnode is { HasExited: false } && _blender is { HasExited: false };

    public async Task WaitForExportAsync(CancellationToken cancellationToken)
    {
        var blenderExited = _blender!.WaitForExitAsync(cancellationToken);
        var hnodeExited = _hnode!.WaitForExitAsync(cancellationToken);
        var first = await Task.WhenAny(_exportDone.Task, _blenderError.Task, blenderExited, hnodeExited);
        cancellationToken.ThrowIfCancellationRequested();

        // Blender quits itself after the export; its exit only completes once its output has been read,
        // so a finished export is always seen before (or with) the exit.
        if (_exportDone.Task.IsCompleted)
            return;
        if (_blenderError.Task.IsCompleted)
            throw new RenderSessionException(_blenderError.Task.Result);
        if (first == blenderExited)
            throw new RenderSessionException($"Blender exited before the frame export finished (exit code {_blender.ExitCode}).");
        throw new RenderSessionException($"HNode exited before the frame export finished (exit code {_hnode.ExitCode}).");
    }

    private static async Task<string> WriteSetupScriptAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, SetupScriptResource);
        await using var resource = typeof(RenderSession).Assembly.GetManifestResourceStream(SetupScriptResource)
            ?? throw new InvalidOperationException($"Embedded resource {SetupScriptResource} is missing.");
        await using var file = File.Create(path);
        await resource.CopyToAsync(file, cancellationToken);
        return path;
    }

    private void WriteBlenderLog(string? line)
    {
        if (line is null)
            return;
        lock (this)
            _blenderLog?.WriteLine(line);

        if (line == ExportDoneLine)
        {
            _exportDone.TrySetResult();
        }
        else if (line.StartsWith(ErrorPrefix, StringComparison.Ordinal))
        {
            _logger.LogError("Blender: {Message}", line[ErrorPrefix.Length..]);
            _blenderError.TrySetResult(line[ErrorPrefix.Length..]);
        }
        else if (line.StartsWith(StatusPrefix, StringComparison.Ordinal))
        {
            _logger.LogInformation("Blender: {Message}", line[StatusPrefix.Length..]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // After a finished export the setup script quits Blender itself; give it a moment to do so.
        if (_exportDone.Task.IsCompleted && _blender is { HasExited: false })
        {
            using var quitWait = new CancellationTokenSource(BlenderSelfQuitWait);
            try { await _blender.WaitForExitAsync(quitWait.Token); }
            catch (OperationCanceledException) { }
        }
        await CloseAsync(_blender, "Blender");
        await CloseAsync(_hnode, "HNode");
        lock (this)
        {
            _blenderLog?.Dispose();
            _blenderLog = null;
        }
    }

    private async Task CloseAsync(Process? process, string name)
    {
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
            {
                if (process.CloseMainWindow())
                {
                    using var grace = new CancellationTokenSource(CloseGracePeriod);
                    try { await process.WaitForExitAsync(grace.Token); }
                    catch (OperationCanceledException) { }
                }
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                _logger.LogInformation("Closed {Name}", name);
            }
        }
        catch (InvalidOperationException)
        {
            // never started / already gone
        }
        finally
        {
            process.Dispose();
        }
    }
}
