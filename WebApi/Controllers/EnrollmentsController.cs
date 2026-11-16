using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using WebApi.Contracts;
using WebApi.Services;

namespace WebApi.Controllers;

[ApiController]
[Route("enrollments")]
public sealed class EnrollmentsController(IEnrollmentService enrollmentService) : BaseApiController
{
    [HttpGet("{id}")]
    [ProducesResponseType<EnrollmentDetailsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<EnrollmentDetailsResponse>> GetDetails(
        [FromRoute] long id,
        CancellationToken ct)
    {
        var details = await enrollmentService.GetDetailsAsync(id, CurrentUserId, ct);
        if (details is null)
            return Problem(statusCode: StatusCodes.Status404NotFound);

        return Ok(details);
    }

    [HttpPost("{id}/seats")]
    [ProducesResponseType<EnrollmentDetailsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<EnrollmentDetailsResponse>> ChangeSeats(
        [FromRoute] long id,
        [FromRequiredBody, Required] ChangeSeatsRequest request,
        CancellationToken ct)
    {
        var result = await enrollmentService.ChangeSeatsAsync(id, CurrentUserId, request.Seats, ct);

        return result switch
        {
            ChangeSeatsResult.Success success => Ok(success.Details),
            ChangeSeatsResult.NotFound => Problem(statusCode: StatusCodes.Status404NotFound),
            ChangeSeatsResult.Conflict conflict => ConflictWithReason(conflict.Reason),
            _ => throw new InvalidOperationException("Unexpected seat change result.")
        };
    }

    [HttpPost("{id}/cancel")]
    [ProducesResponseType<CancelEnrollmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CancelEnrollmentResponse>> Cancel(
        [FromRoute] long id,
        CancellationToken ct)
    {
        var result = await enrollmentService.CancelAsync(id, CurrentUserId, ct);

        return result switch
        {
            CancelEnrollmentResult.Success success => Ok(success.Response),
            CancelEnrollmentResult.NotFound => Problem(statusCode: StatusCodes.Status404NotFound),
            CancelEnrollmentResult.Conflict conflict => ConflictWithReason(conflict.Reason),
            _ => throw new InvalidOperationException("Unexpected enrollment cancellation result.")
        };
    }

    [HttpPost]
    [ProducesResponseType<EnrollmentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<EnrollmentResponse>> Create(
        [FromRequiredBody] ClassSeatsRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required] string idempotencyKey,
        CancellationToken ct)
    {
        var result = await enrollmentService.CreateAsync(
            CurrentUserId, request.ClassId, request.Seats, idempotencyKey, ct);

        return result switch
        {
            CreateEnrollmentResult.Success success => new ContentResult
            {
                Content = success.ResponseBody,
                ContentType = "application/json; charset=utf-8",
                StatusCode = StatusCodes.Status201Created
            },
            CreateEnrollmentResult.ClassNotFound => Problem(statusCode: StatusCodes.Status404NotFound),
            CreateEnrollmentResult.Conflict conflict => ConflictWithReason(conflict.Reason),
            _ => throw new InvalidOperationException("Unexpected enrollment result.")
        };
    }
}
