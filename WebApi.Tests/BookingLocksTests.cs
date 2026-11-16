using System.Data;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WebApi.Services;

namespace WebApi.Tests;

[TestClass]
public class BookingLocksTests
{
    [TestMethod]
    [DataRow("active", false, false, null)]
    [DataRow("cancelled", false, false, "enrollment-cancelled")]
    [DataRow("active", true, false, "class-cancelled")]
    [DataRow("active", false, true, "class-started")]
    public async Task SeatChangeChecksOwnershipAndStateEvenWithoutADelta(
        string status, bool classCancelled, bool classStarted, string? reason)
    {
        await using var factory = new ApiApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = (ClubDbContext)unit.DbContext;
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        var service = new EnrollmentService(unit, new TestClock(now));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            service.PrepareSeatChangeAsync(1, 1, 2, default));

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var enrollment = await AddEnrollmentAsync(db, classStarted ? now : now.AddHours(1));
        enrollment.Status = status;
        if (status == "cancelled")
        {
            enrollment.CancelledAt = now.UtcDateTime;
            enrollment.RefundedVisits = enrollment.Seats;
        }
        enrollment.Class.IsCancelled = classCancelled;
        await db.SaveChangesAsync();

        Assert.IsInstanceOfType<SeatChangePreparation.NotFound>(
            await service.PrepareSeatChangeAsync(enrollment.Id, long.MaxValue, enrollment.Seats, default));
        Assert.IsInstanceOfType<SeatChangePreparation.NotFound>(
            await service.PrepareSeatChangeAsync(long.MaxValue, enrollment.UserId, enrollment.Seats, default));

        var result = await service.PrepareSeatChangeAsync(enrollment.Id, enrollment.UserId, enrollment.Seats, default);
        if (reason is null)
        {
            Assert.IsInstanceOfType<SeatChangePreparation.Ready>(result);
            var ready = (SeatChangePreparation.Ready)result;
            Assert.AreEqual(0, ready.SeatDelta);
            Assert.IsNull(ready.Pass);
        }
        else
        {
            Assert.AreEqual(new SeatChangePreparation.Conflict(reason), result);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SeatChangeUsesFreshStateAfterLockWait(bool waitForClass)
    {
        await using var factory = new ApiApplicationFactory();
        await using var blockerScope = factory.Services.CreateAsyncScope();
        await using var changeScope = factory.Services.CreateAsyncScope();
        var blocker = blockerScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var change = changeScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = (ClubDbContext)blocker.DbContext;
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        var clock = new TestClock(now);
        var enrollment = await AddEnrollmentAsync(db, now.AddHours(1));
        var service = new EnrollmentService(change, clock);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var blockingTransaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (waitForClass)
        {
            Assert.IsNotNull(await blocker.Class.LockByIdAsync(enrollment.ClassId, ct));
            enrollment.Seats = 4;
            await db.SaveChangesAsync(ct);
        }
        else
        {
            Assert.HasCount(1, await blocker.Pass.LockByUsersAsync([enrollment.UserId], ct));
        }

        await using var changeTransaction = await change.DbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var changePid = ((NpgsqlConnection)change.DbContext.Database.GetDbConnection()).ProcessID;
        var preparation = service.PrepareSeatChangeAsync(enrollment.Id, enrollment.UserId, 3, ct);
        try
        {
            while (!preparation.IsCompleted && !await db.Database.SqlQuery<bool>(
                $"""SELECT pg_backend_pid() = ANY(pg_blocking_pids({changePid})) AS "Value" """).SingleAsync(ct))
            {
                await Task.Delay(20, ct);
            }
            Assert.IsFalse(preparation.IsCompleted);
            if (!waitForClass)
                clock.SetUtcNow(now.AddHours(1));

            await blockingTransaction.CommitAsync(ct);
            var result = await preparation;
            if (waitForClass)
            {
                Assert.IsInstanceOfType<SeatChangePreparation.Ready>(result);
                var ready = (SeatChangePreparation.Ready)result;
                Assert.AreEqual(4, ready.Enrollment.Seats);
                Assert.AreEqual(-1, ready.SeatDelta);
                Assert.AreEqual(enrollment.UserId, ready.Pass?.UserId);
            }
            else
            {
                Assert.AreEqual(new SeatChangePreparation.Conflict("class-started"), result);
            }
        }
        finally
        {
            await timeout.CancelAsync();
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ConflictingLockWaitsAndRetryReadsCommittedState(bool sameClass)
    {
        await using var factory = new ApiApplicationFactory();
        await using var firstScope = factory.Services.CreateAsyncScope();
        await using var secondScope = factory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var second = secondScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = (ClubDbContext)first.DbContext;
        var classIds = await db.Classes.OrderBy(x => x.Id).Select(x => x.Id).Take(2).ToArrayAsync();
        var pass = await db.Passes.AsNoTracking().OrderBy(x => x.Id).FirstAsync();
        var userIds = new[] { pass.UserId };
        var originalClass = await second.Class.GetByIdAsync(classIds[0]);
        var originalPass = await second.Pass.GetByUserAsync(pass.UserId);
        Assert.IsNotNull(originalClass);
        Assert.IsNotNull(originalPass);

        await using var firstTransaction = await db.Database.BeginTransactionAsync();
        Assert.IsNotNull(await first.Class.LockByIdAsync(classIds[0]));
        if (!sameClass)
            Assert.AreEqual(pass.Id, (await first.Pass.LockByUsersAsync(userIds)).Single().Id);

        await using (var secondTransaction = await second.DbContext.Database.BeginTransactionAsync())
        {
            await second.DbContext.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '500ms'");
            InvalidOperationException error;
            if (sameClass)
            {
                error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    second.Class.LockByIdAsync(classIds[0]));
            }
            else
            {
                Assert.IsNotNull(await second.Class.LockByIdAsync(classIds[1]));
                error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    second.Pass.LockByUsersAsync(userIds));
            }

            Assert.IsInstanceOfType<PostgresException>(error.InnerException);
            Assert.AreEqual(PostgresErrorCodes.LockNotAvailable, ((PostgresException)error.InnerException).SqlState);
            await secondTransaction.RollbackAsync();
        }

        if (sameClass)
        {
            await db.Classes.Where(x => x.Id == classIds[0])
                .ExecuteUpdateAsync(x => x.SetProperty(c => c.IsCancelled, true));
        }
        else
        {
            await db.Passes.Where(x => x.Id == pass.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.RemainingVisits, pass.RemainingVisits + 1));
        }

        await firstTransaction.CommitAsync();
        try
        {
            await using var retry = await second.DbContext.Database.BeginTransactionAsync();
            if (sameClass)
            {
                var lockedClass = await second.Class.LockByIdAsync(classIds[0]);
                Assert.IsNotNull(lockedClass);
                Assert.IsTrue(lockedClass.IsCancelled);
                Assert.IsFalse(originalClass.IsCancelled);
            }
            else
            {
                Assert.IsNotNull(await second.Class.LockByIdAsync(classIds[1]));
                var lockedPass = (await second.Pass.LockByUsersAsync(userIds)).Single();
                Assert.AreEqual(pass.RemainingVisits + 1, lockedPass.RemainingVisits);
                Assert.AreEqual(pass.RemainingVisits, originalPass.RemainingVisits);
            }
        }
        finally
        {
            if (sameClass)
                await db.Classes.IgnoreQueryFilters().Where(x => x.Id == classIds[0])
                    .ExecuteUpdateAsync(x => x.SetProperty(c => c.IsCancelled, false));
            else
                await db.Passes.Where(x => x.Id == pass.Id)
                    .ExecuteUpdateAsync(x => x.SetProperty(p => p.RemainingVisits, pass.RemainingVisits));
        }
    }

    [TestMethod]
    public async Task LocksRequireTransactionAndPassesAreReturnedInIdOrder()
    {
        await using var factory = new ApiApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => unitOfWork.Class.LockByIdAsync(1));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => unitOfWork.Pass.LockByUsersAsync([1]));

        var db = (ClubDbContext)unitOfWork.DbContext;
        var classId = await db.Classes.Select(x => x.Id).FirstAsync();
        var passes = await db.Passes.OrderBy(x => x.Id).Take(2).ToArrayAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.IsNull(await unitOfWork.Class.LockByIdAsync(long.MaxValue));
        Assert.IsNotNull(await unitOfWork.Class.LockByIdAsync(classId));
        var locked = await unitOfWork.Pass.LockByUsersAsync([passes[1].UserId, passes[0].UserId, passes[1].UserId]);
        CollectionAssert.AreEqual(passes.Select(x => x.Id).ToArray(), locked.Select(x => x.Id).ToArray());
        Assert.AreEqual(0, (await unitOfWork.Pass.LockByUsersAsync([])).Count);
    }

    private static async Task<Enrollment> AddEnrollmentAsync(ClubDbContext db, DateTimeOffset startsAt)
    {
        var userId = await db.Passes.OrderBy(x => x.Id).Select(x => x.UserId).FirstAsync();
        var roomId = await db.Rooms.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
        var enrollment = new Enrollment
        {
            UserId = userId,
            Seats = 2,
            Status = "active",
            CreatedAt = startsAt.UtcDateTime.AddDays(-1),
            Class = new Class
            {
                RoomId = roomId,
                Title = "Seat change lock test",
                StartsAt = startsAt.UtcDateTime,
                DurationMinutes = 45,
                Capacity = 10
            }
        };
        db.Enrollments.Add(enrollment);
        await db.SaveChangesAsync();
        return enrollment;
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private long _utcTicks = now.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void SetUtcNow(DateTimeOffset value) => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);
    }
}
