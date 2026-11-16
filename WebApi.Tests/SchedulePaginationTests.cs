using System.Buffers.Binary;
using Microsoft.AspNetCore.WebUtilities;
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
    public void PagesUseStrictPairOrderAndTokenFromLastReturnedItem()
    {
        ScheduleItemResponse[] rows =
        [
            Item(3, Start), Item(1, Start.AddTicks(1)), Item(4, Start),
            Item(2, Start), Item(6, Start.AddTicks(-1))
        ];
        var items = rows.AsQueryable();

        var first = ScheduleService.BuildPage(items, new ScheduleQuery { Limit = 2 });
        CollectionAssert.AreEqual(new long[] { 6, 2 }, first.Items.Select(item => item.ClassId).ToArray());
        Assert.AreEqual(new ScheduleCursor(Start, 2).Encode(), first.ContinuationToken);

        var second = ScheduleService.BuildPage(items,
            new ScheduleQuery { Limit = 1, ContinuationToken = first.ContinuationToken });
        Assert.AreEqual(3L, second.Items.Single().ClassId);
        Assert.AreEqual(new ScheduleCursor(Start, 3).Encode(), second.ContinuationToken);

        var last = ScheduleService.BuildPage(items,
            new ScheduleQuery { Limit = 2, ContinuationToken = second.ContinuationToken });
        CollectionAssert.AreEqual(new long[] { 4, 1 }, last.Items.Select(item => item.ClassId).ToArray());
        Assert.IsNull(last.ContinuationToken);

        var empty = ScheduleService.BuildPage(items,
            new ScheduleQuery { Limit = 2, ContinuationToken = new ScheduleCursor(Start.AddTicks(1), 1).Encode() });
        Assert.AreEqual(0, empty.Items.Count);
        Assert.IsNull(empty.ContinuationToken);
    }

    private static ScheduleItemResponse Item(long id, DateTime startsAt) =>
        new(id, "Yoga", startsAt, 60, new ScheduleRoomResponse(1, "Studio"), 10, 10, 0);
}
