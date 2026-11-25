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

        Data.Entities.User? user = null;
        if (int.TryParse(idClaim, out var userId) && stampClaim is not null)
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            user = await db.Users.AsNoTracking()
                .SingleOrDefaultAsync(u => u.Id == userId && u.SecurityStamp == stampClaim, context.HttpContext.RequestAborted);
        }

        if (user is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AuthConstants.UserScheme);
            return;
        }

        // Role changes take effect on the next request, without signing the user out.
        if (principal!.FindFirstValue(ClaimTypes.Role) != user.Role.ToString())
        {
            context.ReplacePrincipal(UserService.CreatePrincipal(user));
            context.ShouldRenew = true;
        }
    }
}
