using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Auth;

namespace WebApi.Controllers;

[Authorize]
public abstract class BaseApiController : ControllerBase
{
    protected const string AdminRole = ApiAuthorization.AdminRole;

    protected long CurrentUserId => ApiAuthorization.TryGetUserId(User, out var userId)
        ? userId
        : throw new InvalidOperationException("The authenticated user has no valid ID.");

    protected bool IsAdmin => User.IsInRole(AdminRole);
}
