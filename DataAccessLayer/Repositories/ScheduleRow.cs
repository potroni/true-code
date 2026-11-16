namespace DataAccessLayer.Repositories;

public sealed record ScheduleRow(
    long ClassId,
    string Title,
    DateTime StartsAt,
    int DurationMinutes,
    long RoomId,
    string RoomName,
    int Capacity,
    int FreeSeats,
    int MySeats);
