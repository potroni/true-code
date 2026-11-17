using System.Data;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace WebApi.Services;

internal sealed class IdempotentWriteExecutor(IUnitOfWork unitOfWork)
{
    public async Task<TResult> ExecuteAsync<TResult>(
        long userId, IdempotencyOperation operation, string key, long classId, int seats,
        Func<IdempotencyLookup, TResult> toExistingResult,
        Func<Task<(TResult Result, string? ResponseBody, Func<TResult?>? ValidateBeforeCommit)>> execute,
        CancellationToken ct) where TResult : class
    {
        await using var transaction = await unitOfWork.DbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var journal = unitOfWork.Idempotency;
        await journal.LockAsync(userId, operation, key, ct);
        var lookup = await FindJournalEntryAsync();
        if (lookup.Status != IdempotencyLookupStatus.Missing)
            return toExistingResult(lookup);

        var (result, responseBody, validateBeforeCommit) = await execute();
        if (responseBody is null)
            return result;

        journal.AddSuccess(userId, operation, key, classId, seats, responseBody);
        try
        {
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ux_idempotency_entries_user_operation_hash"
        })
        {
            await transaction.RollbackAsync(ct);
            unitOfWork.DbContext.ChangeTracker.Clear();
            return toExistingResult(await FindJournalEntryAsync());
        }

        if (validateBeforeCommit?.Invoke() is { } rejection)
        {
            await transaction.RollbackAsync(ct);
            unitOfWork.DbContext.ChangeTracker.Clear();
            return rejection;
        }

        await transaction.CommitAsync(ct);
        return result;

        Task<IdempotencyLookup> FindJournalEntryAsync() =>
            journal.FindAsync(userId, operation, key, classId, seats, ct);
    }
}
