using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Server.Data;
using Server.Data.Entities;

namespace Server.Jobs.Tasks;

public partial class TaskResultService(
    AppDbContext db,
    JobPaths paths,
    TaskProgressService progress,
    IOptions<JobStorageOptions> storageOptions,
    ILogger<TaskResultService> logger)
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [GeneratedRegex(@"(-?\d+)\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex FrameNumberPattern();

    public static string FrameFileName(int frame) => $"frame_{frame:D6}.png";

    public async Task<(Job Job, int FramesStored)> CompleteAsync(
        long taskId, int renderMachineId, Stream zipBody, CancellationToken cancellationToken)
    {
        // Cheap checks before reading a potentially large body. They are re-checked under the job
        // lock when the task is marked Completed.
        var task = await db.JobTasks.AsNoTracking().Include(t => t.Job)
            .SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new TaskProgressException($"Task {taskId} not found.", TaskProgressError.NotFound);
        if (task.RenderMachineId != renderMachineId)
            throw new TaskProgressException(task.RenderMachineId is null
                ? "Task has not been claimed. Claim it with POST /api/tasks/claim first."
                : "Task is assigned to a different render machine.", TaskProgressError.Conflict);
        if (task.Status is not (JobTaskStatus.Assigned or JobTaskStatus.Running))
            throw new TaskProgressException($"Task is {task.Status.ToString().ToLowerInvariant()} and can't be completed.", TaskProgressError.Conflict);

        var expectedFrames = Enumerable.Range(task.FrameStart, task.FrameCount).ToHashSet();

        var staging = Path.Combine(paths.IncomingDirectory(task.JobId), $"{taskId}-{Guid.NewGuid():N}");
        var stagedFrames = Path.Combine(staging, "frames");
        Directory.CreateDirectory(stagedFrames);
        try
        {
            // ZipArchive needs a seekable stream, so spool the upload to disk first.
            var zipPath = Path.Combine(staging, "frames.zip");
            await using (var zipFile = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await zipBody.CopyToAsync(zipFile, cancellationToken);

            var received = await ExtractAndValidateAsync(zipPath, stagedFrames, expectedFrames, cancellationToken);

            // Everything checked out: move the frames into place (overwriting any from an earlier attempt).
            var resultDirectory = paths.ResultFramesDirectory(task.JobId);
            Directory.CreateDirectory(resultDirectory);
            foreach (var frame in received)
            {
                var name = FrameFileName(frame);
                File.Move(Path.Combine(stagedFrames, name), Path.Combine(resultDirectory, name), overwrite: true);
            }

            var job = await progress.MarkCompletedAsync(taskId, task.FrameCount, renderMachineId, cancellationToken);
            logger.LogInformation("Task {TaskId} (job {JobId} #{TaskIndex}) completed: {Frames} frames stored in {Directory}",
                taskId, task.JobId, task.TaskIndex, received.Count, resultDirectory);
            return (job, received.Count);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private async Task<List<int>> ExtractAndValidateAsync(
        string zipPath, string destination, HashSet<int> expectedFrames, CancellationToken cancellationToken)
    {
        var maxFrameBytes = storageOptions.Value.MaxResultFrameBytes;
        var received = new List<int>();

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException)
        {
            throw new TaskProgressException("The upload is not a valid zip file.");
        }

        using (archive)
        {
            var seen = new HashSet<int>();
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    continue; // directory entry

                // Only the bare file name is used, so entry paths like "../../x.png" can't escape.
                var fileName = Path.GetFileName(entry.FullName.Replace('\\', '/'));
                var match = FrameNumberPattern().Match(fileName);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out var frame))
                    throw new TaskProgressException($"'{entry.FullName}' is not a PNG frame (expected names like 0042.png).");
                if (!expectedFrames.Contains(frame))
                    throw new TaskProgressException($"'{entry.FullName}' is frame {frame}, which is not part of this task.");
                if (!seen.Add(frame))
                    throw new TaskProgressException($"Frame {frame} appears more than once in the zip.");
                if (entry.Length > maxFrameBytes)
                    throw new TaskProgressException($"'{entry.FullName}' exceeds the maximum frame size.");

                await using var source = entry.Open();
                await using var target = new FileStream(Path.Combine(destination, FrameFileName(frame)),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await CopyFrameAsync(source, target, entry.FullName, maxFrameBytes, cancellationToken);
                received.Add(frame);
            }

            var missing = expectedFrames.Except(seen).Order().ToList();
            if (missing.Count > 0)
                throw new TaskProgressException(
                    $"The zip is missing {missing.Count} frame(s): {string.Join(", ", missing.Take(10))}{(missing.Count > 10 ? ", …" : "")}.");
        }
        return received;
    }

    private static async Task CopyFrameAsync(Stream source, Stream target, string entryName, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        var signatureChecked = false;
        int read;
        while ((read = await source.ReadAtLeastAsync(buffer, PngSignature.Length, throwOnEndOfStream: false, cancellationToken)) > 0)
        {
            if (!signatureChecked)
            {
                if (read < PngSignature.Length || !buffer.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
                    throw new TaskProgressException($"'{entryName}' is not a valid PNG file.");
                signatureChecked = true;
            }

            total += read;
            if (total > maxBytes) // the zip's declared size can lie; enforce while decompressing
                throw new TaskProgressException($"'{entryName}' exceeds the maximum frame size.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (!signatureChecked)
            throw new TaskProgressException($"'{entryName}' is empty.");
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove staging directory {Directory}", directory);
        }
    }
}
