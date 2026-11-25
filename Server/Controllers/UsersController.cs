using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Auth;
using Server.Data;

namespace Server.Controllers;

[Route("users")]
public class UsersController(AppDbContext db, UserService users) : Controller
{
    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        ViewData["CurrentUserId"] = CurrentUserId;
        return View(await db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync(cancellationToken));
    }

    [HttpGet("new")]
    public IActionResult Create() => View();

    [HttpPost("new")]
    public async Task<IActionResult> Create(string username, string password, string confirmPassword, CancellationToken cancellationToken)
    {
        try
        {
            if (password != confirmPassword)
                throw new UserValidationException("Passwords do not match.");
            var user = await users.CreateAsync(username, password, cancellationToken);
            TempData["Message"] = $"Created user \"{user.Username}\".";
            return RedirectToAction(nameof(Index));
        }
        catch (UserValidationException ex)
        {
            ViewData["Error"] = ex.Message;
            ViewData["Username"] = username;
            return View();
        }
    }

    [HttpGet("{id:int}/password")]
    public async Task<IActionResult> ChangePassword(int id, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        return user is null ? NotFound() : View(user);
    }

    [HttpPost("{id:int}/password")]
    public async Task<IActionResult> ChangePassword(int id, string password, string confirmPassword, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
            return NotFound();

        try
        {
            if (password != confirmPassword)
                throw new UserValidationException("Passwords do not match.");
            await users.ChangePasswordAsync(id, password, cancellationToken);
        }
        catch (UserValidationException ex)
        {
            ViewData["Error"] = ex.Message;
            return View(user);
        }

        // Changing a password signs out all of that user's sessions. If it was our own,
        // re-issue our cookie with the new security stamp so we stay signed in.
        if (id == CurrentUserId)
        {
            var updated = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id, cancellationToken);
            await HttpContext.SignInAsync(AuthConstants.UserScheme, UserService.CreatePrincipal(updated));
        }

        TempData["Message"] = $"Changed the password for \"{user.Username}\". Their other sessions were signed out.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:int}/delete")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var username = await db.Users.Where(u => u.Id == id).Select(u => u.Username).SingleOrDefaultAsync(cancellationToken);
        if (username is null)
            return NotFound();

        try
        {
            await users.DeleteAsync(id, CurrentUserId, cancellationToken);
            TempData["Message"] = $"Deleted user \"{username}\".";
        }
        catch (UserValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Index));
    }
}
