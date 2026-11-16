using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class EnrollmentRepository : GenericRepository<Enrollment>, IEnrollmentRepository
{
    private readonly DbSet<Pass> _passes;

    public EnrollmentRepository(DbSet<Enrollment> dbSet, DbSet<Pass> passes)
        : base(dbSet)
    {
        _passes = passes;
    }

    public async Task<List<Enrollment>> GetByUserAsync(long userId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);
    }

    public Task<EnrollmentDetails?> GetDetailsAsync(
        long enrollmentId, long userId, CancellationToken ct = default)
    {
        // решение: отключаем фильтр занятий для карточки, чтобы показывать и отмененные клубом занятия.
        var query =
            from enrollment in OwnedEnrollment(enrollmentId, userId).IgnoreQueryFilters()
            join pass in _passes on enrollment.UserId equals pass.UserId
            let activity = enrollment.Class
            select new EnrollmentDetails(
                enrollment.Id,
                enrollment.Status,
                enrollment.Seats,
                enrollment.CreatedAt,
                activity.Id,
                activity.Title,
                activity.StartsAt,
                activity.DurationMinutes,
                activity.IsCancelled,
                activity.Room.Id,
                activity.Room.Name,
                pass.RemainingVisits,
                pass.ValidUntil);

        return query.SingleOrDefaultAsync(ct);
    }

    public Task<long?> FindOwnedClassIdAsync(long enrollmentId, long userId, CancellationToken ct = default) =>
        OwnedEnrollment(enrollmentId, userId)
            .Select(x => (long?)x.ClassId)
            .SingleOrDefaultAsync(ct);

    public Task<Enrollment?> FindOwnedAsync(long enrollmentId, long userId, CancellationToken ct = default) =>
        OwnedEnrollment(enrollmentId, userId).AsNoTracking().SingleOrDefaultAsync(ct);

    private IQueryable<Enrollment> OwnedEnrollment(long enrollmentId, long userId) =>
        _dbSet.Where(x => x.Id == enrollmentId && x.UserId == userId);

    public Task<long> SumActiveSeatsAsync(long classId, CancellationToken ct = default) =>
        _dbSet.Where(x => x.ClassId == classId && x.Status == "active")
            .SumAsync(x => (long)x.Seats, ct);
}

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
