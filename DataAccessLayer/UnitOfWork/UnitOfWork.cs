using Common.Database.UnitOfWork;
using DataAccessLayer.Configurations;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataAccessLayer.UnitOfWork;

public interface IUnitOfWork : IUnitOfWorkBase
{
    IRoomRepository Room { get; }

    IClassRepository Class { get; }

    IEnrollmentRepository Enrollment { get; }

    IIdempotencyJournal Idempotency { get; }

    IPassRepository Pass { get; }
}

public class UnitOfWork : UnitOfWorkBase, IUnitOfWork
{
    public UnitOfWork(ClubDbContext dbContext)
        : base(dbContext)
    {
    }

    public IIdempotencyJournal Idempotency =>
        GetRepository<IIdempotencyJournal>(context => new IdempotencyJournal(context));

    public IRoomRepository Room => GetRepository<Room, IRoomRepository>(set => new RoomRepository(set));

    public IClassRepository Class => GetRepository<Class, IClassRepository>(set => new ClassRepository(set));

    public IEnrollmentRepository Enrollment =>
        GetRepository<Enrollment, IEnrollmentRepository>(set => new EnrollmentRepository(set));

    public IPassRepository Pass => GetRepository<Pass, IPassRepository>(set => new PassRepository(set));

    public override async Task SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            await base.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: IdempotencyEntryConfiguration.ScopeIndexName
        })
        {
            throw new IdempotencyKeyAlreadyExistsException(exception);
        }
    }
}
