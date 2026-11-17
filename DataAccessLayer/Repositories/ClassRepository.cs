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

    public IQueryable<Class> QueryClassesForLoad(DateOnly from, DateOnly to, long? roomId)
    {
        var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        // решение: задаем минимальную границу литералом, чтобы Npgsql не передал ее как -infinity.
        var classes = from == DateOnly.MinValue
            ? _dbSet.FromSqlRaw("SELECT * FROM classes WHERE starts_at >= TIMESTAMPTZ '0001-01-01 00:00:00+00'")
            : _dbSet.Where(x => x.StartsAt >= fromUtc);

        if (to < DateOnly.MaxValue)
        {
            var untilUtc = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            classes = classes.Where(x => x.StartsAt < untilUtc);
        }
        else
        {
            // решение: используем последнюю хранимую микросекунду, так как следующая полночь вне диапазона DateTime.
            var lastMicrosecondUtc = to.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                .AddTicks(TimeSpan.TicksPerDay - TimeSpan.TicksPerMicrosecond);
            classes = classes.Where(x => x.StartsAt <= lastMicrosecondUtc);
        }

        if (roomId is long room)
            classes = classes.Where(x => x.RoomId == room);

        return classes.AsNoTracking();
    }

    public Task<List<LoadRow>> GetLoadRowsAsync(DateOnly from, DateOnly to, long? roomId,
        CancellationToken ct = default)
    {
        return QueryClassesForLoad(from, to, roomId)
            .Select(x => new
            {
                Date = DateOnly.FromDateTime(x.StartsAt),
                x.RoomId,
                RoomName = x.Room.Name,
                x.Capacity,
                BookedSeats = x.Enrollments.Where(e => e.Status == "active").Sum(e => (long)e.Seats)
            })
            .GroupBy(x => new { x.Date, x.RoomId, x.RoomName })
            .OrderBy(group => group.Key.Date)
            .ThenBy(group => group.Key.RoomId)
            .Select(group => new LoadRow(group.Key.Date, group.Key.RoomId, group.Key.RoomName,
                group.Count(), (int)group.Sum(x => (long)x.Capacity), (int)group.Sum(x => x.BookedSeats)))
            .ToListAsync(ct);
    }

    public Task<List<ScheduleRow>> GetScheduleRowsAsync(DateTime from, DateTime to, long? roomId, long userId,
        (DateTime StartsAt, long ClassId)? after, int take, CancellationToken ct = default)
    {
        var fromRemainder = from.Ticks % TimeSpan.TicksPerMicrosecond;
        var toRemainder = to.Ticks % TimeSpan.TicksPerMicrosecond;
        var fromMicrosecond = from.AddTicks(-fromRemainder);
        var toMicrosecond = to.AddTicks(-toRemainder);

        // решение: сравниваем с целыми микросекундами и меняем знак сравнения для дробных границ.
        var classes = fromRemainder == 0
            ? _dbSet.Where(x => x.StartsAt >= fromMicrosecond)
            : _dbSet.Where(x => x.StartsAt > fromMicrosecond);
        classes = toRemainder == 0
            ? classes.Where(x => x.StartsAt < toMicrosecond)
            : classes.Where(x => x.StartsAt <= toMicrosecond);

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
