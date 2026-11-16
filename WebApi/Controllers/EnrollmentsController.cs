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
        [FromRoute, Range(typeof(long), "1", "9223372036854775807")] long id,
        CancellationToken ct)
    {
        var details = await enrollmentService.GetDetailsAsync(id, CurrentUserId, ct);
        if (details is null)
            return Problem(statusCode: StatusCodes.Status404NotFound);

        return Ok(details);
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
