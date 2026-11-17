using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using WebApi.Attributes;
using WebApi.Contracts;
using WebApi.Services;

namespace WebApi.Controllers;

[ApiController]
[Route("enrollments/waitlist")]
[Tags("Enrollments")]
public sealed class WaitlistController(IWaitlistService waitlistService) : BaseApiController
{
    [HttpPost]
    [ProducesResponseType<JoinWaitlistResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<JoinWaitlistResponse>> JoinWaitlist(
        [FromRequiredBody, Required] JoinWaitlistRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required] string idempotencyKey,
        CancellationToken ct)
    {
        var result = await waitlistService.JoinWaitlistAsync(
            CurrentUserId, request.ClassId, request.Seats, idempotencyKey, ct);

        return result switch
        {
            JoinWaitlistResult.Success success => new ContentResult
            {
                Content = success.ResponseBody,
                ContentType = "application/json; charset=utf-8",
                StatusCode = StatusCodes.Status201Created
            },
            JoinWaitlistResult.ClassNotFound => Problem(statusCode: StatusCodes.Status404NotFound),
            JoinWaitlistResult.Conflict conflict => ConflictWithReason(conflict.Reason),
            _ => throw new InvalidOperationException("Unexpected waitlist result.")
        };
    }

    [HttpPost("{id}/cancel")]
    [ProducesResponseType<CancelWaitlistResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CancelWaitlistResponse>> CancelWaitlist([FromRoute] long id, CancellationToken ct)
    {
        var result = await waitlistService.CancelWaitlistAsync(id, CurrentUserId, ct);

        return result switch
        {
            CancelWaitlistResult.Success success => Ok(success.Response),
            CancelWaitlistResult.NotFound => Problem(statusCode: StatusCodes.Status404NotFound),
            CancelWaitlistResult.Conflict conflict => ConflictWithReason(conflict.Reason),
            _ => throw new InvalidOperationException("Unexpected waitlist cancellation result.")
        };
    }
}
