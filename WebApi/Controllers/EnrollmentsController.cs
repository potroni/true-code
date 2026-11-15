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
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<ActionResult<EnrollmentResponse>> Create(
        [FromRequiredBody] ClassSeatsRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required] string idempotencyKey,
        CancellationToken ct)
    {
        var enrollment = await enrollmentService.CreateAsync(
            CurrentUserId, request.ClassId, request.Seats, idempotencyKey, ct);

        return enrollment is null
            ? Problem(statusCode: StatusCodes.Status501NotImplemented)
            : StatusCode(StatusCodes.Status201Created, enrollment);
    }
}
