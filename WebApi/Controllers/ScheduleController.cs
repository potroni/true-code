using Microsoft.AspNetCore.Mvc;
using WebApi.Contracts;
using WebApi.Services;

namespace WebApi.Controllers;

[ApiController]
[Route("schedule")]
public sealed class ScheduleController(IScheduleService scheduleService) : BaseApiController
{
    [HttpGet]
    [ProducesResponseType<ScheduleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ScheduleResponse>> Get([FromQuery] ScheduleQuery query, CancellationToken ct) =>
        Ok(await scheduleService.GetPageAsync(query, CurrentUserId, ct));
}
