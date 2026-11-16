namespace DataAccessLayer.Repositories;

public sealed record LoadRow(
    DateOnly Date,
    long RoomId,
    string RoomName,
    int ClassesCount,
    int TotalCapacity,
    int BookedSeats);
