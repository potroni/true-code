using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApi.Auth;
using WebApi.Contracts.Analytics;
using WebApi.Services;

namespace WebApi.Controllers;

[ApiController]
[Route("analytics")]
[Authorize(Policy = ApiAuthorization.AdminPolicy)]
public sealed class AnalyticsController(IAnalyticsService analyticsService) : BaseApiController
{
    [HttpGet("load")]
    [ProducesResponseType<RoomLoadResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<RoomLoadResponse>> GetLoad([FromQuery(Name = "")] RoomLoadQuery query, CancellationToken ct) =>
        Ok(await analyticsService.GetLoadAsync(query, ct));
}
