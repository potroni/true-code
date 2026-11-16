using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public interface IWaitlistRepository : IGenericRepository<WaitlistEntry>
{
    Task<List<WaitlistEntry>> GetWaitingAsync(long classId, CancellationToken ct = default);
}

public class WaitlistRepository(DbSet<WaitlistEntry> dbSet)
    : GenericRepository<WaitlistEntry>(dbSet), IWaitlistRepository
{
    public Task<List<WaitlistEntry>> GetWaitingAsync(long classId, CancellationToken ct = default) =>
        _dbSet.AsNoTracking()
            .Where(x => x.ClassId == classId && x.Status == WaitlistStatus.Waiting)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
}
