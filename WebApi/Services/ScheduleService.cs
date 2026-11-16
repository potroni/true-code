using WebApi.Contracts;

namespace WebApi.Services;

public interface IScheduleService
{
    ScheduleResponse GetPage(ScheduleQuery query, long userId);
}

internal sealed class ScheduleService : IScheduleService
{
    public ScheduleResponse GetPage(ScheduleQuery query, long userId) =>
        BuildPage(Enumerable.Empty<ScheduleItemResponse>().AsQueryable(), query);

    internal static ScheduleResponse BuildPage(IQueryable<ScheduleItemResponse> items, ScheduleQuery query)
    {
        if (ScheduleCursor.TryDecode(query.ContinuationToken, out var cursor))
        {
            items = items.Where(item => item.StartsAt > cursor.StartsAt ||
                (item.StartsAt == cursor.StartsAt && item.ClassId > cursor.ClassId));
        }

        var page = items.OrderBy(item => item.StartsAt)
            .ThenBy(item => item.ClassId)
            .Take(query.Limit + 1)
            .ToList();

        if (page.Count <= query.Limit)
            return new ScheduleResponse(page, null);

        page.RemoveAt(query.Limit);
        var last = page[^1];
        return new ScheduleResponse(page, new ScheduleCursor(last.StartsAt, last.ClassId).Encode());
    }
}
