using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WebApi.Auth;
using WebApi.Middleware;

namespace WebApi.Controllers;

[Authorize]
[EnableRateLimiting(BusinessRateLimitPolicy.Name)]
public abstract class BaseApiController : ControllerBase
{
    protected const string AdminRole = ApiAuthorization.AdminRole;

    protected long CurrentUserId => ApiAuthorization.TryGetUserId(User, out var userId)
        ? userId
        : throw new InvalidOperationException("The authenticated user has no valid ID.");

    protected bool IsAdmin => User.IsInRole(AdminRole);

    protected ObjectResult ConflictWithReason(string reason) =>
        Problem(statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["reason"] = reason });   //todo: think about separate service or fabric
}
