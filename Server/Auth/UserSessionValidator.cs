using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Server.Data;

namespace Server.Auth;

public static class UserSessionValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var idClaim = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var stampClaim = principal?.FindFirstValue(AuthConstants.SecurityStampClaim);

        var valid = false;
        if (int.TryParse(idClaim, out var userId) && stampClaim is not null)
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            valid = await db.Users.AnyAsync(u => u.Id == userId && u.SecurityStamp == stampClaim,
                context.HttpContext.RequestAborted);
        }

        if (!valid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AuthConstants.UserScheme);
        }
    }
}
