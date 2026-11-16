namespace DataAccessLayer.Repositories;

public sealed record EnrollmentDetails(
    long Id,
    string Status,
    int Seats,
    DateTime CreatedAt,
    long ClassId,
    string ClassTitle,
    DateTime StartsAt,
    int DurationMinutes,
    bool IsClassCancelled,
    long RoomId,
    string RoomName,
    int RemainingVisits,
    DateOnly ValidUntil);
