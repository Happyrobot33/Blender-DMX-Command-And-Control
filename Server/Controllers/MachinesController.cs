using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Auth;
using Server.Data;
using Server.Data.Entities;

namespace Server.Controllers;

[Route("machines")]
public class MachinesController(AppDbContext db, RenderMachineService machines) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(await db.RenderMachines.AsNoTracking().OrderBy(m => m.Name).ToListAsync(cancellationToken));

    [HttpGet("new")]
    public IActionResult Create() => View();

    [HttpPost("new")]
    public async Task<IActionResult> Create(string name, CancellationToken cancellationToken)
    {
        try
        {
            var (machine, token) = await machines.CreateAsync(name, cancellationToken);
            return ShowToken(machine, token, isNew: true);
        }
        catch (RenderMachineValidationException ex)
        {
            ViewData["Error"] = ex.Message;
            ViewData["Name"] = name;
            return View();
        }
    }

    [HttpGet("{id:int}/rename")]
    public async Task<IActionResult> Rename(int id, CancellationToken cancellationToken)
    {
        var machine = await db.RenderMachines.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        return machine is null ? NotFound() : View(machine);
    }

    [HttpPost("{id:int}/rename")]
    public async Task<IActionResult> Rename(int id, string name, CancellationToken cancellationToken)
    {
        var machine = await db.RenderMachines.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (machine is null)
            return NotFound();

        try
        {
            var oldName = machine.Name;
            var renamed = await machines.RenameAsync(id, name, cancellationToken);
            TempData["Message"] = $"Renamed \"{oldName}\" to \"{renamed.Name}\".";
            return RedirectToAction(nameof(Index));
        }
        catch (RenderMachineValidationException ex)
        {
            ViewData["Error"] = ex.Message;
            ViewData["Name"] = name;
            return View(machine);
        }
    }

    [HttpPost("{id:int}/token")]
    public async Task<IActionResult> RegenerateToken(int id, CancellationToken cancellationToken) =>
        await Handle(async () =>
        {
            var (machine, token) = await machines.RegenerateTokenAsync(id, cancellationToken);
            return ShowToken(machine, token, isNew: false);
        });

    [HttpPost("{id:int}/enable")]
    public async Task<IActionResult> Enable(int id, CancellationToken cancellationToken) =>
        await Handle(async () =>
        {
            var machine = await machines.SetEnabledAsync(id, true, cancellationToken);
            TempData["Message"] = $"Enabled \"{machine.Name}\".";
            return RedirectToAction(nameof(Index));
        });

    [HttpPost("{id:int}/disable")]
    public async Task<IActionResult> Disable(int id, CancellationToken cancellationToken) =>
        await Handle(async () =>
        {
            var machine = await machines.SetEnabledAsync(id, false, cancellationToken);
            TempData["Message"] = $"Disabled \"{machine.Name}\".";
            return RedirectToAction(nameof(Index));
        });

    [HttpPost("{id:int}/delete")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken) =>
        await Handle(async () =>
        {
            var machine = await machines.DeleteAsync(id, cancellationToken);
            TempData["Message"] = $"Deleted \"{machine.Name}\".";
            return RedirectToAction(nameof(Index));
        });

    private ViewResult ShowToken(RenderMachine machine, string token, bool isNew)
    {
        Response.Headers.CacheControl = "no-store";
        ViewData["Token"] = token;
        ViewData["IsNew"] = isNew;
        return View("Token", machine);
    }

    private async Task<IActionResult> Handle(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (RenderMachineValidationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index));
        }
    }
}
