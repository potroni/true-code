using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class LoadClassSelectionTests
{
    [TestMethod]
    public async Task SelectsOnlyScheduledClassesWithinUtcDatesAndOptionalRoom()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        await using var db = OpenContext();
        var from = new DateOnly(2076, 5, 10);
        var to = from.AddDays(1);
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = start.AddDays(2);
        var room = new Room { Name = "Load selection room" };
        var otherRoom = new Room { Name = "Load selection annex" };
        var atStart = NewClass(room, start);
        var beforeEnd = NewClass(room, end.AddMicroseconds(-1));
        var elsewhere = NewClass(otherRoom, start.AddDays(1));
        var cancelled = NewClass(room, start.AddHours(1));
        cancelled.IsCancelled = true;
        db.AddRange(atStart, beforeEnd, elsewhere, cancelled,
            NewClass(room, start.AddMicroseconds(-1)), NewClass(room, end));
        await db.SaveChangesAsync();

        var repository = new ClassRepository(db.Classes, db.Database);
        CollectionAssert.AreEquivalent(new[] { atStart.Id, beforeEnd.Id, elsewhere.Id },
            await repository.QueryClassesForLoad(from, to, null).Select(x => x.Id).ToArrayAsync());
        CollectionAssert.AreEquivalent(new[] { atStart.Id, beforeEnd.Id },
            await repository.QueryClassesForLoad(from, to, room.Id).Select(x => x.Id).ToArrayAsync());
        Assert.AreEqual(atStart.Id,
            await repository.QueryClassesForLoad(from, from, room.Id).Select(x => x.Id).SingleAsync());
    }

    [TestMethod]
    public async Task SupportsFirstAndLastCalendarDates()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        await using var db = OpenContext();
        var room = new Room { Name = "Load calendar limits room" };
        var first = DateOnly.MinValue.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var last = DateOnly.MaxValue.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var atFirst = NewClass(room, first.AddHours(1));
        var beforeFirst = NewClass(room, first.AddHours(2));
        var atLast = NewClass(room, last.AddTicks(TimeSpan.TicksPerDay - TimeSpan.TicksPerMicrosecond));
        var afterLast = NewClass(room, last.AddHours(1));
        db.AddRange(atFirst, beforeFirst, atLast, afterLast);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE classes SET starts_at = TIMESTAMPTZ '0001-01-01 00:00:00+00' WHERE id = {atFirst.Id}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE classes SET starts_at = TIMESTAMPTZ '0001-12-31 23:59:59.999999+00 BC' WHERE id = {beforeFirst.Id}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE classes SET starts_at = TIMESTAMPTZ '10000-01-01 00:00:00+00' WHERE id = {afterLast.Id}
            """);

        var repository = new ClassRepository(db.Classes, db.Database);
        Assert.AreEqual(atFirst.Id,
            await repository.QueryClassesForLoad(DateOnly.MinValue, DateOnly.MinValue, room.Id)
                .Select(x => x.Id).SingleAsync());
        Assert.AreEqual(atLast.Id,
            await repository.QueryClassesForLoad(DateOnly.MaxValue, DateOnly.MaxValue, room.Id)
                .Select(x => x.Id).SingleAsync());
    }

    [TestMethod]
    public async Task AggregatesActiveBookingsByUtcDateAndRoomWithinInclusivePeriod()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        await using var db = OpenContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var from = new DateOnly(2076, 6, 10);
        var nextDay = from.AddDays(1);
        var to = from.AddDays(2);
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var nextMidnight = start.AddDays(1);
        var end = start.AddDays(3);
        var room = new Room { Name = "Load aggregation room" };
        var annex = new Room { Name = "Load aggregation annex" };
        var cancelledOnlyRoom = new Room { Name = "Load aggregation cancelled only room" };
        var booked = NewClass(room, start);
        var empty = NewClass(room, nextMidnight.AddMicroseconds(-1));
        empty.Capacity = 15;
        var annexClass = NewClass(annex, start.AddHours(3));
        var afterMidnight = NewClass(room, nextMidnight);
        var lastClass = NewClass(annex, end.AddMicroseconds(-1));
        var cancelled = NewClass(room, start.AddHours(2));
        cancelled.IsCancelled = true;
        var cancelledOnly = NewClass(cancelledOnlyRoom, nextMidnight);
        cancelledOnly.IsCancelled = true;
        var userId = await db.Users.OrderBy(user => user.Id).Select(user => user.Id).FirstAsync();
        db.AddRange(lastClass, afterMidnight, annexClass, empty, booked, cancelled, cancelledOnly,
            NewClass(room, start.AddMicroseconds(-1)), NewClass(annex, end),
            new Enrollment { Class = booked, UserId = userId, Seats = 2, Status = "active", CreatedAt = start },
            new Enrollment { Class = booked, UserId = userId, Seats = 3, Status = "active", CreatedAt = start },
            new Enrollment
            {
                Class = booked, UserId = userId, Seats = 4, Status = "cancelled", CreatedAt = start,
                CancelledAt = start, RefundedVisits = 4
            },
            new Enrollment { Class = cancelled, UserId = userId, Seats = 6, Status = "active", CreatedAt = start },
            new Enrollment { Class = cancelledOnly, UserId = userId, Seats = 7, Status = "active", CreatedAt = start });
        await db.SaveChangesAsync();

        var repository = new ClassRepository(db.Classes, db.Database);
        var firstDayRoom = new LoadRow(from, room.Id, room.Name, 2, 25, 5);
        var firstDayAnnex = new LoadRow(from, annex.Id, annex.Name, 1, 10, 0);
        var nextDayRoom = new LoadRow(nextDay, room.Id, room.Name, 1, 10, 0);
        var lastDayAnnex = new LoadRow(to, annex.Id, annex.Name, 1, 10, 0);
        var expected = new[] { firstDayRoom, firstDayAnnex }.OrderBy(row => row.RoomId)
            .Append(nextDayRoom).Append(lastDayAnnex).ToArray();

        await db.Database.ExecuteSqlRawAsync("SET LOCAL TIME ZONE 'UTC'");
        CollectionAssert.AreEqual(expected, await repository.GetLoadRowsAsync(from, to, null));

        await db.Database.ExecuteSqlRawAsync("SET LOCAL TIME ZONE 'America/Los_Angeles'");
        CollectionAssert.AreEqual(expected, await repository.GetLoadRowsAsync(from, to, null));
        CollectionAssert.AreEqual(new[] { firstDayRoom, nextDayRoom },
            await repository.GetLoadRowsAsync(from, to, room.Id));
        Assert.AreEqual(0, (await repository.GetLoadRowsAsync(from, to, cancelledOnlyRoom.Id)).Count);
    }

    private static ClubDbContext OpenContext() => new(new DbContextOptionsBuilder<ClubDbContext>()
        .UseNpgsql(TestDatabase.PostgresConnectionString).Options);

    private static Class NewClass(Room room, DateTime startsAt) => new()
    {
        Room = room,
        Title = "Yoga",
        StartsAt = startsAt,
        DurationMinutes = 60,
        Capacity = 10
    };
}
