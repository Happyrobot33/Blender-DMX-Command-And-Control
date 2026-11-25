using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Server.Data;

namespace Server.Auth;

public class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    AppDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header["Bearer ".Length..].Trim();
        if (!token.StartsWith(ApiTokens.Prefix, StringComparison.Ordinal))
            return AuthenticateResult.Fail("Malformed API token.");

        var hash = ApiTokens.Hash(token);
        var machine = await db.RenderMachines.SingleOrDefaultAsync(m => m.TokenHash == hash, Context.RequestAborted);
        if (machine is null)
            return AuthenticateResult.Fail("Unknown API token.");
        if (!machine.IsEnabled)
            return AuthenticateResult.Fail("Render machine is disabled.");

        var now = DateTime.UtcNow;
        if (machine.LastSeenAtUtc is null || now - machine.LastSeenAtUtc > LastSeenResolution)
        {
            await db.RenderMachines.Where(m => m.Id == machine.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastSeenAtUtc, now), Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, machine.Id.ToString()),
            new Claim(ClaimTypes.Name, machine.Name),
            new Claim(AuthConstants.PrincipalTypeClaim, AuthConstants.MachinePrincipalType),
        ], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Response.WriteAsJsonAsync(new { error = "A valid render machine API token is required." });
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Response.WriteAsJsonAsync(new { error = "This endpoint is for render machines only." });
    }
}
