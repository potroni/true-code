using DataAccessLayer;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace WebApi.Tests;

[TestClass]
public class BookingLocksTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ConflictingLockWaitsAndRetryReadsCommittedState(bool sameClass)
    {
        await using var factory = new ApiApplicationFactory();
        await using var firstScope = factory.Services.CreateAsyncScope();
        await using var secondScope = factory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var second = secondScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = (ClubDbContext)first.DbContext;
        var classIds = await db.Classes.OrderBy(x => x.Id).Select(x => x.Id).Take(2).ToArrayAsync();
        var pass = await db.Passes.AsNoTracking().OrderBy(x => x.Id).FirstAsync();
        var userIds = new[] { pass.UserId };
        var originalClass = await second.Class.GetByIdAsync(classIds[0]);
        var originalPass = await second.Pass.GetByUserAsync(pass.UserId);
        Assert.IsNotNull(originalClass);
        Assert.IsNotNull(originalPass);

        await using var firstTransaction = await db.Database.BeginTransactionAsync();
        Assert.IsNotNull(await first.Class.LockByIdAsync(classIds[0]));
        if (!sameClass)
            Assert.AreEqual(pass.Id, (await first.Pass.LockByUsersAsync(userIds)).Single().Id);

        await using (var secondTransaction = await second.DbContext.Database.BeginTransactionAsync())
        {
            await second.DbContext.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '500ms'");
            InvalidOperationException error;
            if (sameClass)
            {
                error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    second.Class.LockByIdAsync(classIds[0]));
            }
            else
            {
                Assert.IsNotNull(await second.Class.LockByIdAsync(classIds[1]));
                error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    second.Pass.LockByUsersAsync(userIds));
            }

            Assert.IsInstanceOfType<PostgresException>(error.InnerException);
            Assert.AreEqual(PostgresErrorCodes.LockNotAvailable, ((PostgresException)error.InnerException).SqlState);
            await secondTransaction.RollbackAsync();
        }

        if (sameClass)
        {
            await db.Classes.Where(x => x.Id == classIds[0])
                .ExecuteUpdateAsync(x => x.SetProperty(c => c.IsCancelled, true));
        }
        else
        {
            await db.Passes.Where(x => x.Id == pass.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.RemainingVisits, pass.RemainingVisits + 1));
        }

        await firstTransaction.CommitAsync();
        try
        {
            await using var retry = await second.DbContext.Database.BeginTransactionAsync();
            if (sameClass)
            {
                var lockedClass = await second.Class.LockByIdAsync(classIds[0]);
                Assert.IsNotNull(lockedClass);
                Assert.IsTrue(lockedClass.IsCancelled);
                Assert.IsFalse(originalClass.IsCancelled);
            }
            else
            {
                Assert.IsNotNull(await second.Class.LockByIdAsync(classIds[1]));
                var lockedPass = (await second.Pass.LockByUsersAsync(userIds)).Single();
                Assert.AreEqual(pass.RemainingVisits + 1, lockedPass.RemainingVisits);
                Assert.AreEqual(pass.RemainingVisits, originalPass.RemainingVisits);
            }
        }
        finally
        {
            if (sameClass)
                await db.Classes.IgnoreQueryFilters().Where(x => x.Id == classIds[0])
                    .ExecuteUpdateAsync(x => x.SetProperty(c => c.IsCancelled, false));
            else
                await db.Passes.Where(x => x.Id == pass.Id)
                    .ExecuteUpdateAsync(x => x.SetProperty(p => p.RemainingVisits, pass.RemainingVisits));
        }
    }

    [TestMethod]
    public async Task LocksRequireTransactionAndPassesAreReturnedInIdOrder()
    {
        await using var factory = new ApiApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => unitOfWork.Class.LockByIdAsync(1));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => unitOfWork.Pass.LockByUsersAsync([1]));

        var db = (ClubDbContext)unitOfWork.DbContext;
        var classId = await db.Classes.Select(x => x.Id).FirstAsync();
        var passes = await db.Passes.OrderBy(x => x.Id).Take(2).ToArrayAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.IsNull(await unitOfWork.Class.LockByIdAsync(long.MaxValue));
        Assert.IsNotNull(await unitOfWork.Class.LockByIdAsync(classId));
        var locked = await unitOfWork.Pass.LockByUsersAsync([passes[1].UserId, passes[0].UserId, passes[1].UserId]);
        CollectionAssert.AreEqual(passes.Select(x => x.Id).ToArray(), locked.Select(x => x.Id).ToArray());
        Assert.AreEqual(0, (await unitOfWork.Pass.LockByUsersAsync([])).Count);
    }
}
