using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IClassRepository : IGenericRepository<Class>
{
    Task<Class?> LockByIdAsync(long id, CancellationToken ct = default);
    Task<Class?> GetWithEnrollmentsAsync(long id, CancellationToken ct = default);
    Task<List<LoadRow>> GetLoadRowsAsync(DateOnly from, DateOnly to, long? roomId, CancellationToken ct = default);
    Task<List<ScheduleRow>> GetScheduleRowsAsync(DateTime from, DateTime to, long? roomId, long userId,
        (DateTime StartsAt, long ClassId)? after, int take, CancellationToken ct = default);
}

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

public sealed record LoadRow(
    DateOnly Date,
    long RoomId,
    string RoomName,
    int ClassesCount,
    int TotalCapacity,
    int BookedSeats);
