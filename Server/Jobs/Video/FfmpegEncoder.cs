using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Server.Jobs.Video;

public class VideoEncodingException(string message) : Exception(message);

public class FfmpegEncoder(IOptions<VideoOptions> options, ILogger<FfmpegEncoder> logger)
{
    public async Task EncodeAsync(IReadOnlyList<string> frames, int frameRate, string outputPath,
        Func<int, Task> onProgress, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var startInfo = new ProcessStartInfo(settings.ResolveFfmpegPath())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        string[] args =
        [
            "-hide_banner", "-loglevel", "error", "-nostats", "-y",
            "-progress", "pipe:1",
            // Input: PNGs on stdin at the scene's frame rate.
            "-f", "image2pipe", "-c:v", "png", "-framerate", frameRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", "pipe:0",
            .. settings.CodecArguments,
            "-f", "matroska", outputPath,
        ];
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new VideoEncodingException($"Could not start ffmpeg at '{startInfo.FileName}': {ex.Message}");
        }

        using var killOnCancel = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var progressTask = ReadProgressAsync(process.StandardOutput, onProgress);

        // Feed frames. If ffmpeg dies early the pipe breaks; its stderr explains why.
        try
        {
            var stdin = process.StandardInput.BaseStream;
            foreach (var frame in frames)
            {
                await using var file = new FileStream(frame, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await file.CopyToAsync(stdin, cancellationToken);
            }
            await stdin.FlushAsync(cancellationToken);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "ffmpeg closed its input early");
        }
        finally
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
        }

        await process.WaitForExitAsync(CancellationToken.None);
        await progressTask;
        var stderr = await stderrTask;
        cancellationToken.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
        {
            logger.LogWarning("ffmpeg failed (exit {ExitCode}): {Stderr}", process.ExitCode, stderr);
            var detail = stderr.Trim();
            throw new VideoEncodingException($"ffmpeg failed (exit code {process.ExitCode}){(detail.Length > 0 ? ": " + Tail(detail, 500) : ".")}");
        }
    }

    private static async Task ReadProgressAsync(StreamReader output, Func<int, Task> onProgress)
    {
        // -progress emits key=value blocks; "frame=N" is the number of frames encoded so far.
        string? line;
        while ((line = await output.ReadLineAsync()) is not null)
        {
            if (line.StartsWith("frame=", StringComparison.Ordinal) && int.TryParse(line.AsSpan(6), out var frame))
                await onProgress(frame);
        }
    }

    private static string Tail(string text, int max) => text.Length <= max ? text : "…" + text[^max..];
}
