using System.Data;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Services;

public interface IEnrollmentService
{
    Task<CreateEnrollmentResult> CreateAsync(
        long userId, long classId, int seats, string idempotencyKey, CancellationToken ct);
}

public abstract record CreateEnrollmentResult
{
    private CreateEnrollmentResult() { }

    public sealed record Success(string ResponseBody) : CreateEnrollmentResult;

    public sealed record ClassNotFound : CreateEnrollmentResult;

    public sealed record Conflict(string Reason) : CreateEnrollmentResult;

    public sealed record NotImplemented : CreateEnrollmentResult;
}

internal sealed class EnrollmentService(IUnitOfWork unitOfWork, TimeProvider timeProvider) : IEnrollmentService
{
    public async Task<CreateEnrollmentResult> CreateAsync(
        long userId, long classId, int seats, string idempotencyKey, CancellationToken ct)
    {
        var requestedAt = timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = await unitOfWork.DbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        const IdempotencyOperation operation = IdempotencyOperation.CreateEnrollment;
        var journal = unitOfWork.Idempotency;
        await journal.LockAsync(userId, operation, idempotencyKey, ct);
        var lookup = await journal.FindAsync(userId, operation, idempotencyKey, classId, seats, ct);

        return lookup.Status switch
        {
            IdempotencyLookupStatus.Replay when lookup.ResponseBody is { } body =>
                new CreateEnrollmentResult.Success(body),
            IdempotencyLookupStatus.ParameterConflict =>
                new CreateEnrollmentResult.Conflict("idempotency-key-conflict"),
            IdempotencyLookupStatus.Missing => await CheckAvailabilityAsync(userId, classId, seats, requestedAt, ct),
            _ => throw new InvalidOperationException("Unexpected idempotency lookup result.")
        };
    }

    private async Task<CreateEnrollmentResult> CheckAvailabilityAsync(
        long userId, long classId, int seats, DateTime requestedAt, CancellationToken ct)
    {
        var activity = await unitOfWork.Class.LockByIdAsync(classId, ct);
        if (activity is null)
            return new CreateEnrollmentResult.ClassNotFound();
        if (activity.IsCancelled)
            return new CreateEnrollmentResult.Conflict("class-cancelled");
        if (activity.StartsAt <= requestedAt)
            return new CreateEnrollmentResult.Conflict("class-started");

        var occupiedSeats = await unitOfWork.Enrollment.SumActiveSeatsAsync(classId, ct);
        if (activity.Capacity - occupiedSeats < seats)
            return new CreateEnrollmentResult.Conflict("capacity-exceeded");

        var pass = (await unitOfWork.Pass.LockByUsersAsync([userId], ct)).Single();
        if (pass.RemainingVisits < seats)
            return new CreateEnrollmentResult.Conflict("pass-exhausted");
        if (pass.ValidUntil < DateOnly.FromDateTime(requestedAt))
            return new CreateEnrollmentResult.Conflict("pass-expired");

        return new CreateEnrollmentResult.NotImplemented();
    }
}
