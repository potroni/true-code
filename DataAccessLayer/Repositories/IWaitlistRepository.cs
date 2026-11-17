using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IWaitlistRepository : IGenericRepository<WaitlistEntry>
{
    Task<long?> FindOwnedClassIdAsync(long id, long userId, CancellationToken ct);
    Task<WaitlistEntry?> FindOwnedAsync(long id, long userId, CancellationToken ct);
    Task<bool> HasWaitingAsync(long classId, long userId, CancellationToken ct);
    Task<int> CountWaitingAheadAsync(WaitlistEntry entry, CancellationToken ct);
    Task<List<WaitlistEntry>> GetWaitingAsync(long classId, CancellationToken ct = default);
}
