using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public interface IWaitlistRepository : IGenericRepository<WaitlistEntry>
{
    Task<long?> FindOwnedClassIdAsync(long id, long userId, CancellationToken ct);
    Task<WaitlistEntry?> FindOwnedAsync(long id, long userId, CancellationToken ct);
    Task<bool> HasWaitingAsync(long classId, long userId, CancellationToken ct);
    Task<int> CountWaitingAheadAsync(WaitlistEntry entry, CancellationToken ct);
    Task<List<WaitlistEntry>> GetWaitingAsync(long classId, CancellationToken ct = default);
}

public class WaitlistRepository(DbSet<WaitlistEntry> dbSet)
    : GenericRepository<WaitlistEntry>(dbSet), IWaitlistRepository
{
    public Task<long?> FindOwnedClassIdAsync(long id, long userId, CancellationToken ct) =>
        _dbSet.Where(entry => entry.Id == id && entry.UserId == userId)
            .Select(entry => (long?)entry.ClassId)
            .SingleOrDefaultAsync(ct);

    public Task<WaitlistEntry?> FindOwnedAsync(long id, long userId, CancellationToken ct) =>
        _dbSet.AsNoTracking().SingleOrDefaultAsync(entry => entry.Id == id && entry.UserId == userId, ct);

    public Task<bool> HasWaitingAsync(long classId, long userId, CancellationToken ct) =>
        _dbSet.AnyAsync(x => x.ClassId == classId && x.UserId == userId && x.Status == WaitlistStatus.Waiting, ct);

    public Task<int> CountWaitingAheadAsync(WaitlistEntry entry, CancellationToken ct) =>
        _dbSet.CountAsync(x => x.ClassId == entry.ClassId && x.Status == WaitlistStatus.Waiting
            && (x.CreatedAt < entry.CreatedAt || (x.CreatedAt == entry.CreatedAt && x.Id < entry.Id)), ct);

    public Task<List<WaitlistEntry>> GetWaitingAsync(long classId, CancellationToken ct = default) =>
        _dbSet.AsNoTracking()
            .Where(x => x.ClassId == classId && x.Status == WaitlistStatus.Waiting)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
}
