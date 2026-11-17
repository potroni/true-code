using DataAccessLayer.UnitOfWork;
using WebApi.Contracts.Schedule;
using WebApi.Pagination;

namespace WebApi.Services;

public interface IScheduleService
{
    Task<ScheduleResponse> GetPageAsync(ScheduleQuery query, long userId, CancellationToken ct);
}

internal sealed class ScheduleService(IUnitOfWork unitOfWork) : IScheduleService
{
    public async Task<ScheduleResponse> GetPageAsync(ScheduleQuery query, long userId, CancellationToken ct)
    {
        (DateTime, long)? after = ScheduleCursor.TryDecode(query.ContinuationToken, out var cursor)
            ? (cursor.StartsAt, cursor.ClassId)
            : null;
        var rows = await unitOfWork.Class.GetScheduleRowsAsync(
            query.From, query.To, query.RoomId, userId, after, query.Limit + 1, ct);

        var items = rows.Take(query.Limit)
            .Select(row => new ScheduleItemResponse(row.ClassId, row.Title, row.StartsAt, row.DurationMinutes,
                new ScheduleRoomResponse(row.RoomId, row.RoomName), row.Capacity, row.FreeSeats, row.MySeats))
            .ToArray();
        var next = rows.Count > query.Limit
            ? new ScheduleCursor(items[^1].StartsAt, items[^1].ClassId).Encode()
            : null;
        return new ScheduleResponse(items, next);
    }
}
