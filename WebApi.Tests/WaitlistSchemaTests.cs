using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace WebApi.Tests;

[TestClass]
public class WaitlistSchemaTests
{
    [TestMethod]
    public async Task WaitingIsUniquePerUserAndClassAndOrderedByTimeThenId()
    {
        await using var factory = new ApiApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();

        var userIds = await db.Users.OrderBy(x => x.Id).Select(x => x.Id).Take(3).ToArrayAsync();
        var classIds = await db.Classes.OrderBy(x => x.Id).Select(x => x.Id).Take(2).ToArrayAsync();
        var createdAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var entry = new WaitlistEntry { UserId = userIds[0], ClassId = classIds[0], Seats = 2, CreatedAt = createdAt };
        db.WaitlistEntries.Add(entry);
        await db.SaveChangesAsync();

        var retry = new WaitlistEntry { UserId = entry.UserId, ClassId = entry.ClassId, Seats = 1, CreatedAt = createdAt };
        db.WaitlistEntries.Add(retry);
        var error = await Assert.ThrowsExceptionAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertConstraint(error, PostgresErrorCodes.UniqueViolation, "ux_waitlist_entries_waiting_user_class");
        db.Entry(retry).State = EntityState.Detached;

        entry.Status = WaitlistStatus.Cancelled;
        await db.SaveChangesAsync();
        db.WaitlistEntries.Add(retry);
        await db.SaveChangesAsync();

        var sameTime = new WaitlistEntry { UserId = userIds[1], ClassId = entry.ClassId, Seats = 1, CreatedAt = createdAt };
        var earlier = new WaitlistEntry { UserId = userIds[2], ClassId = entry.ClassId, Seats = 1, CreatedAt = createdAt.AddMinutes(-1) };
        var otherClass = new WaitlistEntry { UserId = entry.UserId, ClassId = classIds[1], Seats = 1, CreatedAt = createdAt };
        db.WaitlistEntries.AddRange(sameTime, earlier, otherClass);
        await db.SaveChangesAsync();

        var waitingIds = await db.WaitlistEntries
            .Where(x => x.ClassId == entry.ClassId && x.Status == WaitlistStatus.Waiting)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(x => x.Id)
            .ToArrayAsync();

        CollectionAssert.AreEqual(new[] { earlier.Id, retry.Id, sameTime.Id }, waitingIds);
    }

    [TestMethod]
    [DataRow(0, WaitlistStatus.Waiting, "ck_waitlist_entries_seats")]
    [DataRow(1, (WaitlistStatus)99, "ck_waitlist_entries_status")]
    public async Task InvalidValuesAreRejectedByPostgres(int seats, WaitlistStatus status, string constraint)
    {
        await using var factory = new ApiApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();

        db.WaitlistEntries.Add(new WaitlistEntry
        {
            UserId = await db.Users.Select(x => x.Id).FirstAsync(),
            ClassId = await db.Classes.Select(x => x.Id).FirstAsync(),
            Seats = seats,
            Status = status,
            CreatedAt = DateTime.UtcNow
        });

        var error = await Assert.ThrowsExceptionAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertConstraint(error, PostgresErrorCodes.CheckViolation, constraint);
    }

    private static void AssertConstraint(DbUpdateException error, string sqlState, string constraint)
    {
        Assert.IsInstanceOfType<PostgresException>(error.InnerException);
        var postgresError = (PostgresException)error.InnerException!;
        Assert.AreEqual(sqlState, postgresError.SqlState);
        Assert.AreEqual(constraint, postgresError.ConstraintName);
    }
}
