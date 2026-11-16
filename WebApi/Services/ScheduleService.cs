using WebApi.Contracts;

namespace WebApi.Services;

public interface IScheduleService
{
    ScheduleResponse GetPage(ScheduleQuery query, long userId);
}

internal sealed class ScheduleService : IScheduleService
{
    public ScheduleResponse GetPage(ScheduleQuery query, long userId) => new([], null);
}
