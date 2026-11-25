using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Server.Data;
using Server.Data.Entities;
using Server.Jobs;
using Server.Jobs.Video;

namespace Server.Controllers;

[Route("jobs")]
public class JobsController(
    AppDbContext db,
    JobUploadService uploadService,
    JobPaths paths,
    VideoEncodingQueue videoQueue,
    FramesArchiveService framesArchive,
    IOptions<JobStorageOptions> storageOptions,
    ILogger<JobsController> logger) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var jobs = await db.Jobs.AsNoTracking()
            .Include(j => j.Tasks)
            .OrderByDescending(j => j.CreatedAtUtc)
            .Take(200)
            .ToListAsync(cancellationToken);
        return View(jobs);
    }

    [HttpGet("new")]
    public IActionResult Create()
    {
        ViewData["MaxUploadBytes"] = storageOptions.Value.MaxUploadBytes;
        return View();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking()
            .Include(j => j.Tasks.OrderBy(t => t.TaskIndex)).ThenInclude(t => t.RenderMachine)
            .SingleOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (job is null)
            return NotFound();

        ViewData["SourceDirectory"] = paths.SourceDirectory(job.Id);
        ViewData["SourceFiles"] = paths.ListSourceFiles(job.Id);
        ViewData["ResultFramesDirectory"] = paths.ResultFramesDirectory(job.Id);
        ViewData["ResultFrameCount"] = paths.CountResultFrames(job.Id);
        var videoPath = job.VideoFileName is null ? null : Path.Combine(paths.VideoDirectory(job.Id), job.VideoFileName);
        ViewData["VideoBytes"] = videoPath is not null && System.IO.File.Exists(videoPath) ? new FileInfo(videoPath).Length : (long?)null;
        var framesZip = framesArchive.ArchivePath(job.Id);
        ViewData["FramesZipBytes"] = System.IO.File.Exists(framesZip) ? new FileInfo(framesZip).Length : (long?)null;
        return View(job);
    }

    [HttpPost("{id:guid}/video")]
    public async Task<IActionResult> GenerateVideo(Guid id, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (job is null)
            return NotFound();

        var queued = job.Status == JobStatus.Completed && await db.Jobs
            .Where(j => j.Id == id && j.VideoStatus != VideoStatus.Pending && j.VideoStatus != VideoStatus.Encoding
                        && j.VideoStatus != VideoStatus.Packaging)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.VideoStatus, VideoStatus.Pending)
                .SetProperty(j => j.VideoError, (string?)null)
                .SetProperty(j => j.VideoFramesEncoded, 0), cancellationToken) == 1;
        if (queued)
        {
            videoQueue.Enqueue(id);
            TempData["Message"] = "Video generation queued.";
        }
        else
        {
            TempData["Error"] = job.Status != JobStatus.Completed
                ? "The video can only be generated once every task has completed."
                : "The video is already being generated.";
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet("{id:guid}/frames")]
    public async Task<IActionResult> DownloadFrames(Guid id, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (job is null)
            return NotFound();
        if (job.Status != JobStatus.Completed)
        {
            TempData["Error"] = "Frames can be downloaded once every task has completed.";
            return RedirectToAction(nameof(Details), new { id });
        }

        string archivePath;
        try
        {
            archivePath = await framesArchive.GetOrCreateAsync(id, cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            TempData["Error"] = "The job's rendered frames are missing on disk.";
            return RedirectToAction(nameof(Details), new { id });
        }
        return this.ResumableFile(archivePath, $"{DownloadBaseName(job)}-frames.zip", "application/zip");
    }

    [HttpGet("{id:guid}/video")]
    public async Task<IActionResult> DownloadVideo(Guid id, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (job is null)
            return NotFound();

        var videoPath = job.VideoFileName is null ? null : Path.Combine(paths.VideoDirectory(id), job.VideoFileName);
        if (job.VideoStatus != VideoStatus.Ready || videoPath is null || !System.IO.File.Exists(videoPath))
        {
            TempData["Error"] = "The video isn't available yet.";
            return RedirectToAction(nameof(Details), new { id });
        }
        return this.ResumableFile(videoPath, job.VideoFileName!);
    }

    private static string DownloadBaseName(Job job) =>
        job.VideoFileName is not null ? Path.GetFileNameWithoutExtension(job.VideoFileName) : job.Id.ToString();

    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (job is null)
            return NotFound();

        if (job.IsBusy)
        {
            var activity = job.IsInProgress ? job.Status.ToString().ToLowerInvariant() : "generating its video";
            TempData["Error"] = $"\"{job.Name}\" is still {activity} and can't be deleted yet.";
            return RedirectToAction(nameof(Details), new { id });
        }

        // Files first: if that fails the row stays, so the job is still visible and can be retried.
        try
        {
            paths.DeleteJobDirectory(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not delete directory for job {JobId}", id);
            TempData["Error"] = $"Couldn't delete the files for \"{job.Name}\": {ex.Message}";
            return RedirectToAction(nameof(Details), new { id });
        }

        await db.Jobs.Where(j => j.Id == id).ExecuteDeleteAsync(cancellationToken);
        logger.LogInformation("Deleted job {JobId} ({JobName})", id, job.Name);

        TempData["Message"] = $"Deleted job \"{job.Name}\".";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("upload")]
    [ValidateAntiForgeryToken]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> Upload(CancellationToken cancellationToken)
    {
        var sizeFeature = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = storageOptions.Value.MaxUploadBytes;

        try
        {
            var job = await uploadService.ReceiveAsync(Request, cancellationToken);
            return Ok(new
            {
                id = job.Id,
                status = job.Status.ToString(),
                fileCount = job.FileCount,
                totalBytes = job.TotalBytes,
                detailsUrl = Url.Action(nameof(Details), new { id = job.Id }),
            });
        }
        catch (JobUploadException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Server.Jobs.Analysis.BlendScanException ex)
        {
            return UnprocessableEntity(new { error = ex.Message });
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "The upload exceeds the maximum allowed size." });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Client aborted upload");
            return new EmptyResult();
        }
    }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        factories.RemoveType<FormValueProviderFactory>();
        factories.RemoveType<FormFileValueProviderFactory>();
        factories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
