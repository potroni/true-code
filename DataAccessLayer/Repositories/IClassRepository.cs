using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IClassRepository : IGenericRepository<Class>
{
    Task<Class?> LockByIdAsync(long id, CancellationToken ct = default);
    Task<Class?> GetWithEnrollmentsAsync(long id, CancellationToken ct = default);
    Task<List<ScheduleRow>> GetScheduleRowsAsync(DateTime from, DateTime to, long? roomId, long userId,
        (DateTime StartsAt, long ClassId)? after, int take, CancellationToken ct = default);
}
