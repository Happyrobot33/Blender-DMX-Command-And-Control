using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Auth;
using Server.Data;
using Server.Data.Entities;
using Server.Jobs;

namespace Server.Controllers.Api;

[ApiController]
[Route("api/jobs")]
[Authorize(Policy = AuthConstants.RenderMachinePolicy)]
[IgnoreAntiforgeryToken]
public class JobsApiController(AppDbContext db, SourceArchiveService sourceArchives, JobPaths paths, ILogger<JobsApiController> logger) : ControllerBase
{
    private int MachineId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("{id:guid}/source")]
    public async Task<IActionResult> DownloadSource(Guid id, CancellationToken cancellationToken)
    {
        if (await CheckAccessAsync(id, cancellationToken) is { } denied)
            return denied;

        string archivePath;
        try
        {
            archivePath = await sourceArchives.GetOrCreateAsync(id, cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            return NotFound(new { error = "The job's source files are missing." });
        }

        logger.LogInformation("Machine {MachineId} downloading source for job {JobId}", MachineId, id);
        // Served as a file: Content-Length is set and Range requests let machines resume downloads.
        return this.ResumableFile(archivePath, $"{id}-source.zip", "application/zip");
    }

    [HttpGet("{id:guid}/config")]
    public async Task<IActionResult> DownloadConfig(Guid id, CancellationToken cancellationToken)
    {
        if (await CheckAccessAsync(id, cancellationToken) is { } denied)
            return denied;

        var fileName = await db.Jobs.Where(j => j.Id == id).Select(j => j.ConfigFileName).SingleAsync(cancellationToken);
        var path = fileName is null ? null : Path.Combine(paths.ConfigDirectory(id), fileName);
        if (path is null || !System.IO.File.Exists(path))
            return NotFound(new { error = "This job has no HNode config." });

        return this.ResumableFile(path, fileName!, "application/yaml");
    }

    private async Task<IActionResult?> CheckAccessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (!await db.Jobs.AnyAsync(j => j.Id == jobId, cancellationToken))
            return NotFound(new { error = "Job not found." });

        var hasActiveTask = await db.JobTasks.AnyAsync(t => t.JobId == jobId && t.RenderMachineId == MachineId &&
            (t.Status == JobTaskStatus.Assigned || t.Status == JobTaskStatus.Running), cancellationToken);
        return hasActiveTask ? null : StatusCode(StatusCodes.Status403Forbidden,
            new { error = "This machine has no active task in this job. Claim one with POST /api/tasks/claim." });
    }
}
