using WebApi.Contracts;

namespace WebApi.Services;

public interface IEnrollmentService
{
    Task<EnrollmentResponse?> CreateAsync(
        long userId, long classId, int seats, string idempotencyKey, CancellationToken ct);
}

internal sealed class EnrollmentService : IEnrollmentService
{
    public Task<EnrollmentResponse?> CreateAsync(
        long userId, long classId, int seats, string idempotencyKey, CancellationToken ct) =>
        Task.FromResult<EnrollmentResponse?>(null);
}
