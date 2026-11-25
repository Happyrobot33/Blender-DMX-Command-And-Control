using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Server.Auth;

namespace Server.Controllers.Api;

[ApiController]
[Route("api/machines")]
[Authorize(Policy = AuthConstants.RenderMachinePolicy)]
[IgnoreAntiforgeryToken]
public class MachinesApiController : ControllerBase
{
    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        id = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
        name = User.FindFirstValue(ClaimTypes.Name),
    });
}
