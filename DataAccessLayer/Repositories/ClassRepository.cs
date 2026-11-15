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
}
