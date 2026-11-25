using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Auth;
using Server.Data;
using Server.Data.Entities;
using Server.Jobs;
using Server.Jobs.Tasks;

namespace Server.Controllers.Api;

[ApiController]
[Route("api/tasks")]
[Authorize(Policy = AuthConstants.RenderMachinePolicy)]
[IgnoreAntiforgeryToken]
public class TasksApiController(
    TaskProgressService progressService,
    TaskClaimService claimService,
    TaskResultService resultService,
    IOptions<JobStorageOptions> storageOptions,
    AppDbContext db) : ControllerBase
{
    private int MachineId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("claim")]
    public async Task<IActionResult> Claim(CancellationToken cancellationToken)
    {
        var claim = await claimService.ClaimNextAsync(MachineId, cancellationToken);
        if (claim is null)
            return NoContent();
        var task = claim.Task;

        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == task.JobId, cancellationToken);
        return Ok(new
        {
            resumed = claim.Resumed,
            task = new
            {
                id = task.Id,
                taskIndex = task.TaskIndex,
                status = task.Status.ToString(),
                task.FramesCompleted,
                task.FrameStart,
                task.FrameEnd,
                task.FrameCount,
                task.ClaimedAtUtc,
            },
            job = new
            {
                id = job.Id,
                name = job.Name,
                sceneName = job.SceneName,
                mainBlendFile = job.MainBlendFile,
                sourceUrl = Url.Action(nameof(JobsApiController.DownloadSource), "JobsApi", new { id = job.Id }),
                configFileName = job.ConfigFileName,
                configUrl = job.ConfigFileName is null ? null : Url.Action(nameof(JobsApiController.DownloadConfig), "JobsApi", new { id = job.Id }),
            },
        });
    }

    [HttpPost("{id:long}/progress")]
    public async Task<IActionResult> UpdateProgress(long id, TaskProgressUpdate update, CancellationToken cancellationToken)
    {
        try
        {
            var job = await progressService.UpdateAsync(id, update, MachineId, cancellationToken);
            return Ok(ProgressResponse(job, id));
        }
        catch (TaskProgressException ex)
        {
            return Error(ex);
        }
    }

    [HttpPost("{id:long}/complete")]
    public async Task<IActionResult> Complete(long id, CancellationToken cancellationToken)
    {
        var mediaType = Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (mediaType is not ("application/zip" or "application/x-zip-compressed" or "application/octet-stream"))
            return StatusCode(StatusCodes.Status415UnsupportedMediaType, new { error = "Send the frames as a zip with Content-Type: application/zip." });

        var sizeFeature = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = storageOptions.Value.MaxResultUploadBytes;

        try
        {
            var (job, framesStored) = await resultService.CompleteAsync(id, MachineId, Request.Body, cancellationToken);
            return Ok(new { framesStored, progress = ProgressResponse(job, id) });
        }
        catch (TaskProgressException ex)
        {
            return Error(ex);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "The zip exceeds the maximum allowed size." });
        }
    }

    private static object ProgressResponse(Job job, long taskId)
    {
        var task = job.Tasks.Single(t => t.Id == taskId);
        return new
        {
            task = new
            {
                id = task.Id,
                taskIndex = task.TaskIndex,
                status = task.Status.ToString(),
                task.FrameStart,
                task.FrameEnd,
                task.FrameCount,
                task.FramesCompleted,
                progressPercent = Math.Round(task.ProgressPercent, 2),
                error = task.ErrorMessage,
                failCount = task.FailCount,
            },
            job = new
            {
                id = job.Id,
                status = job.Status.ToString(),
                job.TotalFrames,
                job.FramesCompleted,
                progressPercent = Math.Round(job.ProgressPercent, 2),
                tasksCompleted = job.Tasks.Count(t => t.Status == JobTaskStatus.Completed),
                taskCount = job.Tasks.Count,
                error = job.ErrorMessage,
            },
        };
    }

    private IActionResult Error(TaskProgressException ex)
    {
        var body = new { error = ex.Message };
        return ex.Error switch
        {
            TaskProgressError.NotFound => NotFound(body),
            TaskProgressError.Conflict => Conflict(body),
            _ => BadRequest(body),
        };
    }
}
