using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Server.Auth;

namespace Server.Controllers;

[Route("account")]
public class AccountController(UserService users, ILogger<AccountController> logger) : Controller
{
    [HttpGet("login")]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToLocal(returnUrl);
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthConstants.LoginRateLimit)]
    public async Task<IActionResult> Login(string username, string password, string? returnUrl, CancellationToken cancellationToken)
    {
        var user = await users.VerifyCredentialsAsync(username, password, cancellationToken);
        if (user is null)
        {
            logger.LogWarning("Failed sign-in for {Username} from {RemoteIp}", username, HttpContext.Connection.RemoteIpAddress);
            ViewData["ReturnUrl"] = returnUrl;
            ViewData["Username"] = username;
            ViewData["Error"] = "Invalid username or password.";
            return View();
        }

        await HttpContext.SignInAsync(AuthConstants.UserScheme, UserService.CreatePrincipal(user));
        logger.LogInformation("User {Username} signed in", user.Username);
        return RedirectToLocal(returnUrl);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(AuthConstants.UserScheme);
        return RedirectToAction(nameof(Login));
    }

    // Only redirect within this site, never to a URL supplied by an attacker.
    private IActionResult RedirectToLocal(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction("Index", "Jobs");
}
