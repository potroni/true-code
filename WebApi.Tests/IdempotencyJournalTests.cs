using System.Security.Cryptography;
using System.Text;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.Tests;

[TestClass]
public class IdempotencyJournalTests
{
    private const string FirstResponse = "{\"id\":42,\"position\":4}";

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SameKeyWaitsForCommitOrRollbackAcrossClasses(bool commit)
    {
        await using var factory = new ApiApplicationFactory();
        await using var firstScope = factory.Services.CreateAsyncScope();
        await using var secondScope = factory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var second = secondScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = (ClubDbContext)first.DbContext;
        var userId = await db.Users.Select(x => x.Id).FirstAsync();
        var classes = await db.Classes.OrderBy(x => x.Id).Select(x => x.Id).Take(2).ToArrayAsync();
        var key = $"{Guid.NewGuid():N}{new string('x', 5000)}";
        const IdempotencyOperation operation = IdempotencyOperation.JoinWaitlist;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var firstTransaction = await db.Database.BeginTransactionAsync(ct);
        await using var secondTransaction = await second.DbContext.Database.BeginTransactionAsync(ct);
        var secondPid = await BackendPidAsync(second.DbContext, ct);

        await first.Idempotency.LockAsync(userId, operation, key, ct);
        first.Idempotency.AddSuccess(userId, operation, key, classes[0], 2, FirstResponse);
        await first.SaveChangesAsync(ct);

        var secondLock = second.Idempotency.LockAsync(userId, operation, key, ct);
        try
        {
            await WaitForBlockingAsync(db, secondPid, secondLock, ct);
            if (commit)
                await firstTransaction.CommitAsync(ct);
            else
                await firstTransaction.RollbackAsync(ct);
        }
        finally
        {
            if (!secondLock.IsCompleted && !ct.IsCancellationRequested && db.Database.CurrentTransaction is not null)
                await timeout.CancelAsync();

            await secondLock;
        }

        var otherClass = await second.Idempotency.FindAsync(userId, operation, key, classes[1], 2, ct);
        if (commit)
        {
            Assert.AreEqual(IdempotencyLookupStatus.ParameterConflict, otherClass.Status);
            var otherSeats = await second.Idempotency.FindAsync(userId, operation, key, classes[0], 1, ct);
            Assert.AreEqual(IdempotencyLookupStatus.ParameterConflict, otherSeats.Status);
            var replay = await second.Idempotency.FindAsync(userId, operation, key, classes[0], 2, ct);
            Assert.AreEqual(new IdempotencyLookup(IdempotencyLookupStatus.Replay, FirstResponse), replay);
        }
        else
        {
            Assert.AreEqual(IdempotencyLookupStatus.Missing, otherClass.Status);
            second.Idempotency.AddSuccess(userId, operation, key, classes[1], 2, FirstResponse);
            await second.SaveChangesAsync(ct);
        }
    }

    [TestMethod]
    public async Task UniqueIndexRejectsRacingInsertAndWinnerCanBeReadAfterRollback()
    {
        await using var factory = new ApiApplicationFactory();
        await using var firstScope = factory.Services.CreateAsyncScope();
        await using var secondScope = factory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var second = secondScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = (ClubDbContext)first.DbContext;
        var users = await db.Users.OrderBy(x => x.Id).Select(x => x.Id).Take(2).ToArrayAsync();
        var classes = await db.Classes.OrderBy(x => x.Id).Select(x => x.Id).Take(2).ToArrayAsync();
        var key = Guid.NewGuid().ToString("N");
        const IdempotencyOperation operation = IdempotencyOperation.CreateEnrollment;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var firstTransaction = await db.Database.BeginTransactionAsync(ct);
        await using var secondTransaction = await second.DbContext.Database.BeginTransactionAsync(ct);
        var secondPid = await BackendPidAsync(second.DbContext, ct);

        first.Idempotency.AddSuccess(users[0], operation, key, classes[0], 1, FirstResponse);
        first.Idempotency.AddSuccess(users[1], operation, key, classes[0], 1, FirstResponse);
        first.Idempotency.AddSuccess(users[0], IdempotencyOperation.JoinWaitlist, key, classes[0], 1, FirstResponse);
        await first.SaveChangesAsync(ct);

        second.Idempotency.AddSuccess(users[0], operation, key, classes[1], 1, "{}");
        var secondSave = second.SaveChangesAsync(ct);
        try
        {
            await WaitForBlockingAsync(db, secondPid, secondSave, ct);
            await firstTransaction.CommitAsync(ct);
        }
        finally
        {
            if (!secondSave.IsCompleted && !ct.IsCancellationRequested && db.Database.CurrentTransaction is not null)
                await timeout.CancelAsync();

            await Assert.ThrowsExceptionAsync<IdempotencyKeyAlreadyExistsException>(() => secondSave);
        }

        await secondTransaction.RollbackAsync(ct);
        second.DbContext.ChangeTracker.Clear();
        var replay = await second.Idempotency.FindAsync(users[0], operation, key, classes[0], 1, ct);
        Assert.AreEqual(new IdempotencyLookup(IdempotencyLookupStatus.Replay, FirstResponse), replay);
    }

    [TestMethod]
    public async Task SaveChangesPreservesOtherConstraintViolations()
    {
        await using var factory = new ApiApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = (ClubDbContext)unitOfWork.DbContext;
        var userId = await db.Users.Select(x => x.Id).FirstAsync();
        var classId = await db.Classes.Select(x => x.Id).FirstAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();

        unitOfWork.Idempotency.AddSuccess(
            userId, IdempotencyOperation.CreateEnrollment, Guid.NewGuid().ToString("N"),
            classId, 0, FirstResponse);

        await Assert.ThrowsExceptionAsync<DbUpdateException>(() => unitOfWork.SaveChangesAsync());
    }

    [TestMethod]
    public async Task MatchingHashWithDifferentOriginalKeyDoesNotReplayResponse()
    {
        await using var factory = new ApiApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var key = Guid.NewGuid().ToString("N");
        var entry = new IdempotencyEntry
        {
            UserId = await db.Users.Select(x => x.Id).FirstAsync(),
            Operation = IdempotencyOperation.CreateEnrollment,
            KeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key)),
            OriginalKey = Guid.NewGuid().ToString("N"),
            ClassId = await db.Classes.Select(x => x.Id).FirstAsync(),
            Seats = 1,
            ResponseBody = FirstResponse
        };
        db.IdempotencyEntries.Add(entry);
        await db.SaveChangesAsync();

        var journal = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().Idempotency;
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            journal.FindAsync(entry.UserId, entry.Operation, key, entry.ClassId, entry.Seats));
    }

    private static Task<int> BackendPidAsync(DbContext db, CancellationToken ct) =>
        db.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(ct);

    private static async Task WaitForBlockingAsync(DbContext db, int blockedPid, Task pending, CancellationToken ct)
    {
        while (!pending.IsCompleted)
        {
            if (await db.Database.SqlQuery<bool>(
                    $"SELECT pg_backend_pid() = ANY(pg_blocking_pids({blockedPid})) AS \"Value\"").SingleAsync(ct))
                return;

            await Task.Delay(20, ct);
        }

        Assert.Fail("The second transaction completed without waiting for the first.");
    }
}
