using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using WebApi.Contracts;
using WebApi.Services;

namespace WebApi.Controllers;

[ApiController]
[Route("enrollments")]
public sealed class EnrollmentsController(IEnrollmentService enrollmentService) : BaseApiController
{
    [HttpPost]
    [ProducesResponseType<EnrollmentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
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
            CreateEnrollmentResult.KeyConflict => ConflictWithReason("idempotency-key-conflict"),
            CreateEnrollmentResult.NotImplemented => Problem(statusCode: StatusCodes.Status501NotImplemented),
            _ => throw new InvalidOperationException("Unexpected enrollment result.")
        };
    }
}
