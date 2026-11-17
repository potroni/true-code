using System.Text.Json.Serialization;

namespace WebApi.Contracts.Schedule;

public sealed record ScheduleResponse(
    [property: JsonRequired] IReadOnlyList<ScheduleItemResponse> Items,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ContinuationToken);

public sealed record ScheduleItemResponse(
    long ClassId,
    string Title,
    DateTime StartsAt,
    int DurationMinutes,
    ScheduleRoomResponse Room,
    int Capacity,
    int FreeSeats,
    int MySeats);

public sealed record ScheduleRoomResponse(long Id, string Name);
