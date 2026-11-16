using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DataAccessLayer.Repositories;

public class ClassRepository : GenericRepository<Class>, IClassRepository
{
    private readonly DatabaseFacade _database;

    public ClassRepository(DbSet<Class> dbSet, DatabaseFacade database)
        : base(dbSet)
    {
        _database = database;
    }

    public async Task<Class?> LockByIdAsync(long id, CancellationToken ct = default)
    {
        if (_database.CurrentTransaction is null)
            throw new InvalidOperationException("An active transaction is required to lock a class.");

        // решение: включаем отмененные занятия для отмены действующих броней с возвратом посещений.
        var classes = await _dbSet
            .FromSql($"SELECT * FROM classes WHERE id = {id} FOR NO KEY UPDATE")
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ToListAsync(ct);

        return classes.SingleOrDefault();
    }

    public async Task<Class?> GetWithEnrollmentsAsync(long id, CancellationToken ct = default)
    {
        return await _dbSet
            .Include(x => x.Enrollments)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public Task<List<ScheduleRow>> GetScheduleRowsAsync(DateTime from, DateTime to, long? roomId, long userId,
        (DateTime StartsAt, long ClassId)? after, int take, CancellationToken ct = default)
    {
        var classes = _dbSet.Where(x => x.StartsAt >= from && x.StartsAt < to);

        if (roomId is long room)
            classes = classes.Where(x => x.RoomId == room);

        if (after is { } cursor)
        {
            var (startsAt, classId) = cursor;
            classes = classes.Where(x => x.StartsAt > startsAt || (x.StartsAt == startsAt && x.Id > classId));
        }

        return classes.OrderBy(x => x.StartsAt)
            .ThenBy(x => x.Id)
            .Take(take)
            .Select(x => new ScheduleRow(x.Id, x.Title, x.StartsAt, x.DurationMinutes, x.RoomId, x.Room.Name,
                x.Capacity,
                (int)Math.Max(0L, x.Capacity - x.Enrollments.Where(e => e.Status == "active").Sum(e => (long)e.Seats)),
                x.Enrollments.Where(e => e.Status == "active" && e.UserId == userId).Sum(e => e.Seats)))
            .ToListAsync(ct);
    }
}
