using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IPassRepository : IGenericRepository<Pass>
{
    Task<List<Pass>> LockByUsersAsync(long[] userIds, CancellationToken ct = default);
    Task<Pass?> GetByUserAsync(long userId, CancellationToken ct = default);
}
