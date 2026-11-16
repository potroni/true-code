using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Auth;
using WebApi.Contracts;

namespace WebApi.Controllers;

[ApiController]
[Route("analytics")]
[Authorize(Policy = ApiAuthorization.AdminPolicy)]
public sealed class AnalyticsController : BaseApiController
{
    [HttpGet("load")]
    [ProducesResponseType<LoadResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status501NotImplemented)]
    public ActionResult<LoadResponse> GetLoad([FromQuery] LoadQuery query) =>
        Problem(statusCode: StatusCodes.Status501NotImplemented);
}
