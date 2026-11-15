using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DataAccessLayer.Configurations;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataAccessLayer.Repositories;

public class IdempotencyJournal(ClubDbContext db)
{
    public async Task LockAsync(long userId, IdempotencyOperation operation, string key, CancellationToken ct = default)
    {
        RequireTransaction();
        var lockId = GetLockId(userId, operation, key);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockId})", ct);
    }

    public async Task<IdempotencyLookup> FindAsync(
        long userId, IdempotencyOperation operation, string key, long classId, int seats,
        CancellationToken ct = default)
    {
        var keyHash = HashKey(key);
        var entry = await db.IdempotencyEntries.AsNoTracking().SingleOrDefaultAsync(
            x => x.UserId == userId && x.Operation == operation && x.KeyHash == keyHash, ct);

        if (entry is null)
            return new(IdempotencyLookupStatus.Missing);

        if (!string.Equals(entry.OriginalKey, key, StringComparison.Ordinal))
            throw new InvalidOperationException("Idempotency key hash collision.");

        if (entry.ClassId != classId || entry.Seats != seats)
            return new(IdempotencyLookupStatus.ParameterConflict);

        return new(IdempotencyLookupStatus.Replay, entry.ResponseBody);
    }

    public void AddSuccess(
        long userId, IdempotencyOperation operation, string key, long classId, int seats, string responseBody)
    {
        RequireTransaction();
        db.IdempotencyEntries.Add(new IdempotencyEntry
        {
            UserId = userId,
            Operation = operation,
            KeyHash = HashKey(key),
            OriginalKey = key,
            ClassId = classId,
            Seats = seats,
            ResponseBody = responseBody
        });
    }

    public static bool IsScopeConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: IdempotencyEntryConfiguration.ScopeIndexName
        };

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An active transaction is required for the idempotency journal.");
    }

    private static byte[] HashKey(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private static long GetLockId(long userId, IdempotencyOperation operation, string key)
    {
        // решение: стабильное число из области ключа позволяет синхронизировать запросы разных экземпляров API.
        Span<byte> scope = stackalloc byte[sizeof(long) + sizeof(int) + SHA256.HashSizeInBytes];
        BinaryPrimitives.WriteInt64BigEndian(scope, userId);
        BinaryPrimitives.WriteInt32BigEndian(scope[sizeof(long)..], (int)operation);
        HashKey(key).CopyTo(scope[(sizeof(long) + sizeof(int))..]);
        return BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(scope));
    }
}

public enum IdempotencyLookupStatus
{
    Missing,
    Replay,
    ParameterConflict
}

public sealed record IdempotencyLookup(IdempotencyLookupStatus Status, string? ResponseBody = null);
