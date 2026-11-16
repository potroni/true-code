using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Auth;
using WebApi.Contracts;
using WebApi.Services;

namespace WebApi.Controllers;

[ApiController]
[Route("analytics")]
[Authorize(Policy = ApiAuthorization.AdminPolicy)]
public sealed class AnalyticsController(IAnalyticsService analyticsService) : BaseApiController
{
    [HttpGet("load")]
    [ProducesResponseType<LoadResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<LoadResponse>> GetLoad([FromQuery] LoadQuery query, CancellationToken ct) =>
        Ok(await analyticsService.GetLoadAsync(query, ct));
}
