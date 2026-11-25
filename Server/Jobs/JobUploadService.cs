using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Server.Data;
using Server.Data.Entities;
using Server.Jobs.Analysis;

namespace Server.Jobs;

public class JobUploadException(string message) : Exception(message);

public class JobUploadService(
    AppDbContext db,
    JobPaths paths,
    JobAnalysisQueue analysisQueue,
    IOptions<JobStorageOptions> storageOptions,
    ILogger<JobUploadService> logger)
{
    private const int MaxFieldLength = 4096;

    public async Task<Job> ReceiveAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var boundary = GetBoundary(request.ContentType);
        var reader = new MultipartReader(boundary, request.Body) { BodyLengthLimit = null };

        string? name = null;
        string? sourceFolderName = null;
        string? mainBlendFile = null;
        var taskCount = JobTaskPlanner.DefaultSubTaskCount;
        string? pendingPath = null;
        (string FileName, byte[] Content)? config = null;
        Job? job = null;
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                    throw new JobUploadException("Multipart section is missing a Content-Disposition header.");

                if (disposition.IsFileDisposition() && disposition.Name.Value == "config")
                {
                    if (job is not null || config is not null)
                        throw new JobUploadException("Send exactly one HNode config, before the project files.");
                    var fileName = HNodeConfigValidator.ValidateFileName(disposition.FileName.Value ?? disposition.FileNameStar.Value);
                    var content = await ReadLimitedAsync(section.Body, storageOptions.Value.MaxConfigBytes, "The HNode config", cancellationToken);
                    HNodeConfigValidator.ValidateContent(content);
                    config = (fileName, content);
                }
                else if (disposition.IsFileDisposition())
                {
                    if (job is null && config is null)
                        throw new JobUploadException("Attach the HNode config (.shwcfg) - it must be sent before the project files.");
                    job ??= await CreateJobAsync(name, sourceFolderName, mainBlendFile, config!.Value, cancellationToken);

                    var relativePath = JobPaths.NormalizeRelativePath(pendingPath ?? disposition.FileName.Value)
                        ?? throw new JobUploadException($"Invalid file path '{pendingPath ?? disposition.FileName.Value}'.");
                    pendingPath = null;

                    if (!seenPaths.Add(relativePath))
                        throw new JobUploadException($"Duplicate file path '{relativePath}'.");

                    var size = await WriteFileAsync(job.Id, relativePath, section.Body, cancellationToken);
                    job.FileCount++;
                    job.TotalBytes += size;
                }
                else if (disposition.IsFormDisposition())
                {
                    var value = await ReadFieldAsync(section, cancellationToken);
                    switch (disposition.Name.Value)
                    {
                        case "name": name = value; break;
                        case "sourceFolderName": sourceFolderName = value; break;
                        case "mainBlendFile": mainBlendFile = value; break;
                        case "taskCount": taskCount = ParseTaskCount(value); break;
                        case "path": pendingPath = value; break;
                    }
                }
            }

            if (job is null)
                throw new JobUploadException(config is null
                    ? "Attach the HNode config (.shwcfg) and the project folder."
                    : "The upload did not contain any project files.");

            if (!seenPaths.TryGetValue(job.MainBlendFile, out var uploadedBlendPath))
                throw new JobUploadException($"The selected .blend file '{job.MainBlendFile}' was not part of the upload.");
            job.MainBlendFile = uploadedBlendPath; // use the exact casing that is on disk

            // The .blend scan runs in the background (JobAnalysisWorker), not in this request.
            job.Status = JobStatus.Uploaded;
            job.RequestedTaskCount = taskCount;
            job.CompletedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            analysisQueue.Enqueue(job.Id);
            logger.LogInformation("Job {JobId} uploaded: {FileCount} files, {TotalBytes} bytes; queued for analysis of {BlendFile}",
                job.Id, job.FileCount, job.TotalBytes, job.MainBlendFile);
            return job;
        }
        catch (Exception ex) when (job is not null)
        {
            await MarkFailedAsync(job.Id, ex);
            throw;
        }
    }

    private async Task<Job> CreateJobAsync(string? name, string? sourceFolderName, string? mainBlendFile,
        (string FileName, byte[] Content) config, CancellationToken cancellationToken)
    {
        // Validated before the job exists, so a bad request never leaves a job row behind.
        if (string.IsNullOrWhiteSpace(mainBlendFile))
            throw new JobUploadException("Select the main .blend file (send 'mainBlendFile' before any files).");
        var normalizedBlendFile = JobPaths.NormalizeRelativePath(mainBlendFile);
        if (normalizedBlendFile is null || !JobPaths.IsBlendFile(normalizedBlendFile) || normalizedBlendFile.Length > 1024)
            throw new JobUploadException($"'{mainBlendFile}' is not a valid .blend file path.");

        var now = DateTime.UtcNow;
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Name = Truncate(Coalesce(name, sourceFolderName) ?? $"Job {now:yyyy-MM-dd HH:mm:ss}", 255),
            SourceFolderName = sourceFolderName is null ? null : Truncate(sourceFolderName.Trim(), 255),
            MainBlendFile = normalizedBlendFile,
            ConfigFileName = config.FileName,
            Status = JobStatus.Uploading,
            CreatedAtUtc = now,
        };

        Directory.CreateDirectory(paths.SourceDirectory(job.Id));
        Directory.CreateDirectory(paths.ConfigDirectory(job.Id));
        await File.WriteAllBytesAsync(Path.Combine(paths.ConfigDirectory(job.Id), config.FileName), config.Content, cancellationToken);

        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Created job {JobId} at {JobDirectory}", job.Id, paths.JobDirectory(job.Id));
        return job;
    }

    private async Task<long> WriteFileAsync(Guid jobId, string relativePath, Stream body, CancellationToken cancellationToken)
    {
        var destination = JobPaths.ResolveUnder(paths.SourceDirectory(jobId), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await body.CopyToAsync(file, cancellationToken);
        return file.Length;
    }

    private async Task MarkFailedAsync(Guid jobId, Exception error)
    {
        logger.LogWarning(error, "Upload for job {JobId} failed; removing partial files", jobId);

        var message = Truncate(error is JobUploadException or BlendScanException ? error.Message : "Upload was interrupted or failed on the server.", 2000);
        try
        {
            db.ChangeTracker.Clear();
            await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.ErrorMessage, message)
                .SetProperty(j => j.FileCount, 0)
                .SetProperty(j => j.TotalBytes, 0L)
                .SetProperty(j => j.CompletedAtUtc, DateTime.UtcNow));
        }
        catch (Exception dbError)
        {
            logger.LogError(dbError, "Could not mark job {JobId} as failed", jobId);
        }

        try
        {
            paths.DeleteJobDirectory(jobId);
        }
        catch (Exception ioError)
        {
            logger.LogError(ioError, "Could not remove directory for failed job {JobId}", jobId);
        }
    }

    private static string GetBoundary(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType) ||
            !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new JobUploadException("Expected a multipart/form-data request.");

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
            throw new JobUploadException("Multipart boundary is missing.");
        return boundary;
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream body, long maxBytes, string what, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new JobUploadException($"{what} is larger than the {maxBytes / 1024} KB limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static int ParseTaskCount(string value)
    {
        if (!int.TryParse(value.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
            || count < 1 || count > JobTaskPlanner.MaxSubTaskCount)
            throw new JobUploadException($"Sub task count must be a whole number from 1 to {JobTaskPlanner.MaxSubTaskCount}.");
        return count;
    }

    private static async Task<string> ReadFieldAsync(MultipartSection section, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(section.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var buffer = new char[MaxFieldLength + 1];
        var builder = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            builder.Append(buffer, 0, read);
            if (builder.Length > MaxFieldLength)
                throw new JobUploadException("A form field exceeded the maximum allowed length.");
        }
        return builder.ToString();
    }

    private static string? Coalesce(params string?[] values) =>
        values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
