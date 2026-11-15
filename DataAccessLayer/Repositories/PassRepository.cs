using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DataAccessLayer.Repositories;

public class PassRepository : GenericRepository<Pass>, IPassRepository
{
    private readonly DatabaseFacade _database;

    public PassRepository(DbSet<Pass> dbSet, DatabaseFacade database)
        : base(dbSet)
    {
        _database = database;
    }

    public Task<List<Pass>> LockByUsersAsync(long[] userIds, CancellationToken ct = default)
    {
        if (_database.CurrentTransaction is null)
            throw new InvalidOperationException("An active transaction is required to lock passes.");

        // решение: блокируем весь набор в порядке ID абонемента, чтобы исключить обратный порядок захвата.
        return _dbSet
            .FromSql($"""
                SELECT * FROM passes
                WHERE user_id = ANY({userIds})
                ORDER BY id
                FOR NO KEY UPDATE
                """)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<Pass?> GetByUserAsync(long userId, CancellationToken ct = default)
    {
        return await _dbSet.FirstOrDefaultAsync(x => x.UserId == userId, ct);
    }
}
