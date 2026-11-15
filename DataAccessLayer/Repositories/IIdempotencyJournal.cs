using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IIdempotencyJournal
{
    Task LockAsync(long userId, IdempotencyOperation operation, string key, CancellationToken ct = default);

    Task<IdempotencyLookup> FindAsync(
        long userId, IdempotencyOperation operation, string key, long classId, int seats,
        CancellationToken ct = default);

    void AddSuccess(
        long userId, IdempotencyOperation operation, string key, long classId, int seats, string responseBody);
}
