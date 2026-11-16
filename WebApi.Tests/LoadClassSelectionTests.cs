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
    public async Task AggregatesUtcDayWithoutRepeatingCapacityForMultipleBookings()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        await using var db = OpenContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL TIME ZONE 'America/Los_Angeles'");

        var date = new DateOnly(2076, 6, 10);
        var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var room = new Room { Name = "Load aggregation room" };
        var booked = NewClass(room, midnight);
        var empty = NewClass(room, midnight.AddHours(12));
        var userId = await db.Users.OrderBy(user => user.Id).Select(user => user.Id).FirstAsync();
        db.AddRange(booked, empty,
            new Enrollment { Class = booked, UserId = userId, Seats = 2, Status = "active", CreatedAt = midnight },
            new Enrollment { Class = booked, UserId = userId, Seats = 3, Status = "active", CreatedAt = midnight });
        await db.SaveChangesAsync();

        var repository = new ClassRepository(db.Classes, db.Database);
        var rows = await repository.GetLoadRowsAsync(date, date, room.Id);

        CollectionAssert.AreEqual(new[] { new LoadRow(date, room.Id, room.Name, 2, 20, 5) }, rows);
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
