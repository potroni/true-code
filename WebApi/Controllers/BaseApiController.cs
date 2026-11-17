using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Attributes;
using WebApi.Auth;

namespace WebApi.Controllers;

[Authorize]
[BusinessRateLimit]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
public abstract class BaseApiController : ControllerBase
{
    protected const string AdminRole = ApiAuthorization.AdminRole;

    protected long CurrentUserId => ApiAuthorization.TryGetUserId(User, out var userId)
        ? userId
        : throw new InvalidOperationException("The authenticated user has no valid ID.");

    protected bool IsAdmin => User.IsInRole(AdminRole);

    protected ObjectResult ConflictWithReason(string reason) =>
        Problem(statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["reason"] = reason });
}
