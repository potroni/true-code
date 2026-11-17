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
    [DataRow(4, 2, 0, 6, null)]
    [DataRow(4, 2, 0, 5, "capacity-exceeded")]
    [DataRow(4, 1, 0, 6, "pass-exhausted")]
    [DataRow(4, 2, -1, 6, "pass-expired")]
    [DataRow(1, 0, -1, 6, null)]
    [DataRow(2, 0, -1, 6, null)]
    public async Task SeatChangeAppliesOnlyTheDelta(
        int seats, int remainingVisits, int validityOffset, int capacity, string? expectedConflict)
    {
        await using var factory = new ApiApplicationFactory();
        await using var setupScope = factory.Services.CreateAsyncScope();
        await using var changeScope = factory.Services.CreateAsyncScope();
        var db = setupScope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var unit = changeScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var now = new DateTimeOffset(2026, 11, 16, 23, 30, 0, TimeSpan.Zero);
        var validUntil = DateOnly.FromDateTime(now.UtcDateTime).AddDays(validityOffset);
        var enrollment = await AddEnrollmentAsync(db, now.AddHours(1));
        var otherUserId = await db.Passes.Where(x => x.UserId != enrollment.UserId)
            .Select(x => x.UserId).FirstAsync();
        var pass = await db.Passes.SingleAsync(x => x.UserId == enrollment.UserId);
        var originalVisits = pass.RemainingVisits;
        var originalValidUntil = pass.ValidUntil;
        var waiting = new WaitlistEntry
        {
            ClassId = enrollment.ClassId,
            UserId = otherUserId,
            Seats = 3,
            CreatedAt = now.UtcDateTime
        };
        var otherEnrollment = new Enrollment
        {
            ClassId = enrollment.ClassId,
            UserId = otherUserId,
            Seats = 2,
            Status = "active",
            CreatedAt = now.UtcDateTime
        };
        var cancelledEnrollment = new Enrollment
        {
            ClassId = enrollment.ClassId,
            UserId = otherUserId,
            Seats = 4,
            Status = "cancelled",
            CreatedAt = now.UtcDateTime.AddMinutes(-1),
            CancelledAt = now.UtcDateTime,
            RefundedVisits = 4
        };
        try
        {
            enrollment.Class.Capacity = capacity;
            pass.RemainingVisits = remainingVisits;
            pass.ValidUntil = validUntil;
            db.AddRange(waiting, otherEnrollment, cancelledEnrollment);
            await db.SaveChangesAsync();

            var service = new EnrollmentService(unit, new TestClock(now), new IdempotentWriteExecutor(unit));
            var result = await service.ChangeSeatsAsync(enrollment.Id, enrollment.UserId, seats, default);
            var expectedSeats = expectedConflict is null ? seats : enrollment.Seats;
            var expectedVisits = remainingVisits - (expectedSeats - enrollment.Seats);
            if (expectedConflict is null)
            {
                var success = (ChangeSeatsResult.Success)result;
                Assert.AreEqual(expectedSeats, success.Details.Seats);
                Assert.AreEqual(expectedVisits, success.Details.Pass.RemainingVisits);
                Assert.AreEqual(validUntil, success.Details.Pass.ValidUntil);
                Assert.AreEqual(success, await service.ChangeSeatsAsync(enrollment.Id, enrollment.UserId, seats, default));
            }
            else
            {
                Assert.AreEqual(new ChangeSeatsResult.Conflict(expectedConflict), result);
            }

            Assert.AreEqual(expectedSeats, await db.Enrollments.Where(x => x.Id == enrollment.Id)
                .Select(x => x.Seats).SingleAsync());
            Assert.AreEqual(expectedVisits, await db.Passes.Where(x => x.Id == pass.Id)
                .Select(x => x.RemainingVisits).SingleAsync());
        }
        finally
        {
            db.RemoveRange(waiting, otherEnrollment, cancelledEnrollment, enrollment, enrollment.Class);
            pass.RemainingVisits = originalVisits;
            pass.ValidUntil = originalValidUntil;
            await db.SaveChangesAsync();
        }
    }

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
        await using var changeScope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var unit = changeScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        var service = new EnrollmentService(unit, new TestClock(now), new IdempotentWriteExecutor(unit));

        var enrollment = await AddEnrollmentAsync(db, classStarted ? now : now.AddHours(1));
        enrollment.Status = status;
        if (status == "cancelled")
        {
            enrollment.CancelledAt = now.UtcDateTime;
            enrollment.RefundedVisits = enrollment.Seats;
        }
        enrollment.Class.IsCancelled = classCancelled;
        try
        {
            await db.SaveChangesAsync();
            Assert.IsInstanceOfType<ChangeSeatsResult.NotFound>(
                await service.ChangeSeatsAsync(enrollment.Id, long.MaxValue, enrollment.Seats, default));
            Assert.IsInstanceOfType<ChangeSeatsResult.NotFound>(
                await service.ChangeSeatsAsync(long.MaxValue, enrollment.UserId, enrollment.Seats, default));

            var result = await service.ChangeSeatsAsync(enrollment.Id, enrollment.UserId, enrollment.Seats, default);
            if (reason is null)
            {
                Assert.IsInstanceOfType<ChangeSeatsResult.Success>(result);
                Assert.AreEqual(enrollment.Seats, ((ChangeSeatsResult.Success)result).Details.Seats);
            }
            else
            {
                Assert.AreEqual(new ChangeSeatsResult.Conflict(reason), result);
            }
        }
        finally
        {
            db.RemoveRange(enrollment, enrollment.Class);
            await db.SaveChangesAsync();
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
        var service = new EnrollmentService(change, clock, new IdempotentWriteExecutor(change));
        var pass = await db.Passes.AsNoTracking().SingleAsync(x => x.UserId == enrollment.UserId);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        Task<ChangeSeatsResult>? seatChange = null;
        try
        {
            await change.DbContext.Database.OpenConnectionAsync(ct);
            var requestPid = ((NpgsqlConnection)change.DbContext.Database.GetDbConnection()).ProcessID;
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

            seatChange = service.ChangeSeatsAsync(enrollment.Id, enrollment.UserId, 3, ct);
            while (!seatChange.IsCompleted && !await db.Database.SqlQuery<bool>(
                $"""SELECT pg_backend_pid() = ANY(pg_blocking_pids({requestPid})) AS "Value" """)
                .SingleAsync(ct))
            {
                await Task.Delay(20, ct);
            }
            Assert.IsFalse(seatChange.IsCompleted);
            if (!waitForClass)
                clock.SetUtcNow(now.AddHours(1));

            await blockingTransaction.CommitAsync(ct);
            var result = await seatChange;
            if (waitForClass)
            {
                Assert.IsInstanceOfType<ChangeSeatsResult.Success>(result);
                var details = ((ChangeSeatsResult.Success)result).Details;
                Assert.AreEqual(3, details.Seats);
                Assert.AreEqual(pass.RemainingVisits + 1, details.Pass.RemainingVisits);
            }
            else
            {
                Assert.AreEqual(new ChangeSeatsResult.Conflict("class-started"), result);
            }
        }
        finally
        {
            await timeout.CancelAsync();
            try
            {
                if (seatChange is { IsCompletedSuccessfully: false })
                    await seatChange;
            }
            finally
            {
                db.RemoveRange(enrollment, enrollment.Class);
                await db.SaveChangesAsync();
                await db.Passes.Where(x => x.Id == pass.Id)
                    .ExecuteUpdateAsync(x => x.SetProperty(p => p.RemainingVisits, pass.RemainingVisits));
            }
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

    [TestMethod]
    public async Task CancellationWaitsForCandidatePassAndChecksStartAfterTheWait()
    {
        await using var factory = new ApiApplicationFactory();
        await using var blockerScope = factory.Services.CreateAsyncScope();
        await using var cancellationScope = factory.Services.CreateAsyncScope();
        var blocker = blockerScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var cancellation = cancellationScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = (ClubDbContext)blocker.DbContext;
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        var clock = new TestClock(now);
        var enrollment = await AddEnrollmentAsync(db, now.AddHours(1));
        var candidate = new WaitlistEntry
        {
            ClassId = enrollment.ClassId,
            UserId = await db.Passes.Where(x => x.UserId != enrollment.UserId).Select(x => x.UserId).FirstAsync(),
            Seats = 1,
            CreatedAt = now.UtcDateTime
        };
        db.WaitlistEntries.Add(candidate);
        await db.SaveChangesAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        Task<CancelEnrollmentResult>? request = null;
        try
        {
            var service = new EnrollmentService(cancellation, clock, new IdempotentWriteExecutor(cancellation));
            Assert.AreEqual(new CancelEnrollmentResult.NotFound(),
                await service.CancelAsync(enrollment.Id, candidate.UserId, ct));

            await cancellation.DbContext.Database.OpenConnectionAsync(ct);
            var requestPid = ((NpgsqlConnection)cancellation.DbContext.Database.GetDbConnection()).ProcessID;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            Assert.HasCount(1, await blocker.Pass.LockByUsersAsync([candidate.UserId], ct));
            request = service.CancelAsync(enrollment.Id, enrollment.UserId, ct);
            while (!request.IsCompleted && !await db.Database.SqlQuery<bool>(
                $"""SELECT pg_backend_pid() = ANY(pg_blocking_pids({requestPid})) AS "Value" """)
                .SingleAsync(ct))
            {
                await Task.Delay(20, ct);
            }
            Assert.IsFalse(request.IsCompleted);
            clock.SetUtcNow(now.AddHours(1));
            await transaction.CommitAsync(ct);

            Assert.AreEqual(new CancelEnrollmentResult.Conflict("class-started"), await request);
            Assert.AreEqual("active", await db.Enrollments.Where(x => x.Id == enrollment.Id)
                .Select(x => x.Status).SingleAsync(ct));
        }
        finally
        {
            await timeout.CancelAsync();
            try
            {
                if (request is { IsCompletedSuccessfully: false })
                    await request;
            }
            finally
            {
                db.RemoveRange(candidate, enrollment, enrollment.Class);
                await db.SaveChangesAsync();
            }
        }
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
