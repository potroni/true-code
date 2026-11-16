using System.Buffers.Binary;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WebApi.Contracts;
using WebApi.Services;

namespace WebApi.Tests;

[TestClass]
public class SchedulePaginationTests
{
    private static readonly DateTime Start = new DateTime(2026, 9, 22, 10, 0, 0, DateTimeKind.Utc)
        .AddTicks(1234567);

    [TestMethod]
    public void CursorPreservesUtcTicksAndLongId()
    {
        var original = new ScheduleCursor(Start, long.MaxValue);

        Assert.IsTrue(ScheduleCursor.TryDecode(original.Encode(), out var decoded));
        Assert.AreEqual(original, decoded);
        Assert.AreEqual(DateTimeKind.Utc, decoded.StartsAt.Kind);
    }

    [TestMethod]
    [DataRow(-1L, 1L)]
    [DataRow(long.MaxValue, 1L)]
    [DataRow(1L, 0L)]
    [DataRow(1L, -1L)]
    public void CursorRejectsOutOfRangeValues(long ticks, long id)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(bytes, ticks);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(sizeof(long)), id);

        Assert.IsFalse(ScheduleCursor.TryDecode(WebEncoders.Base64UrlEncode(bytes), out _));
    }

    [TestMethod]
    public async Task ScheduleFiltersClassesAndCountsOnlyActiveSeats()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        var options = new DbContextOptionsBuilder<ClubDbContext>()
            .UseNpgsql(TestDatabase.PostgresConnectionString)
            .Options;
        await using var db = new ClubDbContext(options);
        var from = Start.Date.AddHours(10);
        var to = from.AddHours(2);
        var room = new Room { Name = "Schedule aggregation studio" };
        var otherRoom = new Room { Name = "Schedule aggregation annex" };
        var user = NewUser("schedule-aggregation-member@club.test");
        var otherUser = NewUser("schedule-aggregation-guest@club.test");
        var atFrom = Item(room, from);
        var overCapacity = Item(room, from.AddMinutes(15));
        var withoutBookings = Item(room, from.AddMinutes(30));
        var cancelled = Item(room, from.AddMinutes(45));
        cancelled.IsCancelled = true;
        db.AddRange(user, otherUser, atFrom, overCapacity, withoutBookings, cancelled,
            Item(room, from.AddTicks(-10)), Item(room, to), Item(otherRoom, from));
        await db.SaveChangesAsync();

        db.Enrollments.AddRange(
            Booking(atFrom, user, 1),
            Booking(atFrom, user, 2),
            Booking(atFrom, otherUser, 4),
            Booking(atFrom, user, 5, cancelled: true),
            Booking(atFrom, otherUser, 6, cancelled: true),
            Booking(overCapacity, otherUser, 11));
        await db.SaveChangesAsync();

        var service = new ScheduleService(new UnitOfWork(db));
        var page = await ReadPage(room.Id);
        CollectionAssert.AreEqual(new[] { atFrom.Id, overCapacity.Id, withoutBookings.Id },
            page.Items.Select(item => item.ClassId).ToArray());
        CollectionAssert.AreEqual(new[] { (3, 3), (0, 0), (10, 0) },
            page.Items.Select(item => (item.FreeSeats, item.MySeats)).ToArray());
        Assert.AreEqual(from, page.Items[0].StartsAt);
        Assert.IsNull(page.ContinuationToken);

        var empty = await ReadPage(long.MaxValue);
        Assert.AreEqual(0, empty.Items.Count);
        Assert.IsNull(empty.ContinuationToken);

        Task<ScheduleResponse> ReadPage(long roomId) =>
            service.GetPageAsync(new ScheduleQuery { From = from, To = to, RoomId = roomId },
                user.Id, CancellationToken.None);

        User NewUser(string email) =>
            new() { Email = email, PasswordHash = "unused", Role = "user", CreatedAt = from.AddDays(-1) };

        static Class Item(Room room, DateTime startsAt) =>
            new() { Room = room, Title = "Yoga", StartsAt = startsAt, DurationMinutes = 60, Capacity = 10 };

        Enrollment Booking(Class item, User member, int seats, bool cancelled = false) =>
            new()
            {
                ClassId = item.Id,
                UserId = member.Id,
                Seats = seats,
                Status = cancelled ? "cancelled" : "active",
                CreatedAt = from.AddDays(-1),
                CancelledAt = cancelled ? from.AddHours(-3) : null,
                RefundedVisits = cancelled ? seats : null
            };
    }

    [TestMethod]
    public async Task PagesUseStrictPairOrderAndTokenFromLastReturnedItem()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        var commands = new List<string>();
        var options = new DbContextOptionsBuilder<ClubDbContext>()
            .UseNpgsql(TestDatabase.PostgresConnectionString)
            .LogTo(commands.Add, [RelationalEventId.CommandExecuted])
            .Options;
        await using var db = new ClubDbContext(options);
        var room = new Room { Name = "Schedule pagination studio" };
        var user = new User
        {
            Email = "schedule-pagination@club.test",
            PasswordHash = "unused",
            Role = "user",
            CreatedAt = Start
        };
        var startsAt = Start.AddTicks(-7);
        Class[] classes =
        [
            Item(startsAt.AddTicks(10)), Item(startsAt), Item(startsAt),
            Item(startsAt), Item(startsAt.AddTicks(-10))
        ];
        db.Add(user);
        db.AddRange(classes);
        await db.SaveChangesAsync();
        db.Enrollments.Add(new Enrollment
        {
            ClassId = classes[1].Id,
            UserId = user.Id,
            Seats = 3,
            Status = "active",
            CreatedAt = startsAt.AddDays(-1)
        });
        await db.SaveChangesAsync();
        commands.Clear();

        var service = new ScheduleService(new UnitOfWork(db));
        var tiedIds = classes.Skip(1).Take(3).Select(item => item.Id).Order().ToArray();
        var first = await ReadPage(2);
        CollectionAssert.AreEqual(new[] { classes[4].Id, tiedIds[0] }, first.Items.Select(item => item.ClassId).ToArray());
        Assert.AreEqual(new ScheduleCursor(startsAt, tiedIds[0]).Encode(), first.ContinuationToken);
        var sql = commands.Single();
        StringAssert.Contains(sql, "LIMIT");
        StringAssert.Contains(sql, "sum(");
        StringAssert.Contains(sql, "GREATEST(");

        var second = await ReadPage(1, first.ContinuationToken);
        Assert.AreEqual(tiedIds[1], second.Items.Single().ClassId);
        Assert.AreEqual(new ScheduleCursor(startsAt, tiedIds[1]).Encode(), second.ContinuationToken);

        var last = await ReadPage(2, second.ContinuationToken);
        CollectionAssert.AreEqual(new[] { tiedIds[2], classes[0].Id }, last.Items.Select(item => item.ClassId).ToArray());
        Assert.IsNull(last.ContinuationToken);

        var empty = await ReadPage(2, new ScheduleCursor(classes[0].StartsAt, classes[0].Id).Encode());
        Assert.AreEqual(0, empty.Items.Count);
        Assert.IsNull(empty.ContinuationToken);
        var enrolled = first.Items.Concat(second.Items).Concat(last.Items).Single(x => x.ClassId == classes[1].Id);
        Assert.AreEqual(7, enrolled.FreeSeats);
        Assert.AreEqual(3, enrolled.MySeats);
        Assert.AreEqual(new ScheduleRoomResponse(room.Id, room.Name), enrolled.Room);

        Task<ScheduleResponse> ReadPage(int limit, string? token = null) =>
            service.GetPageAsync(new ScheduleQuery
            {
                From = startsAt.AddTicks(-10),
                To = startsAt.AddTicks(20),
                RoomId = room.Id,
                Limit = limit,
                ContinuationToken = token
            }, user.Id, CancellationToken.None);

        Class Item(DateTime start) =>
            new() { Room = room, Title = "Yoga", StartsAt = start, DurationMinutes = 60, Capacity = 10 };
    }
}
