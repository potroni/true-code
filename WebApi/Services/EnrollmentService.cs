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

    public sealed record KeyConflict : CreateEnrollmentResult;

    public sealed record NotImplemented : CreateEnrollmentResult;
}

internal sealed class EnrollmentService(IUnitOfWork unitOfWork) : IEnrollmentService
{
    public async Task<CreateEnrollmentResult> CreateAsync(
        long userId, long classId, int seats, string idempotencyKey, CancellationToken ct)
    {
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
            IdempotencyLookupStatus.ParameterConflict => new CreateEnrollmentResult.KeyConflict(),
            IdempotencyLookupStatus.Missing => new CreateEnrollmentResult.NotImplemented(),
            _ => throw new InvalidOperationException("Unexpected idempotency lookup result.")
        };
    }
}
