using DataAccessLayer;
using DataAccessLayer.Seeding;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentRefundMigrationTests
{
    private const string PreviousMigration = "20261115060554_AddIdempotencyEntries";

    [TestMethod]
    public async Task MigrationPreservesHistoricalEnrollmentsAndPassBalances()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(TestDatabase.PostgresConnectionString)
        {
            Database = $"club_refund_tests_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<ClubDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new ClubDbContext(options);

        try
        {
            Assert.IsFalse(db.Database.HasPendingModelChanges());
            await db.Database.MigrateAsync();
            await DatabaseSeeder.SeedAsync(db);

            var seedRefunds = await db.Enrollments
                .Select(x => new { x.Status, x.RefundedVisits })
                .ToArrayAsync();
            Assert.IsTrue(seedRefunds.Any(x => x.Status == "active"));
            Assert.IsTrue(seedRefunds.Any(x => x.Status == "cancelled"));
            Assert.IsTrue(seedRefunds.All(x => x.RefundedVisits == (x.Status == "cancelled" ? 0 : (int?)null)));

            await db.Database.MigrateAsync(PreviousMigration);
            var historicalId = await db.Enrollments.Where(x => x.Status == "active")
                .OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE enrollments SET status = 'cancelled', cancelled_at = now() WHERE id = {historicalId}
                """);

            var balances = await ReadBalancesAsync(db);
            await db.Database.MigrateAsync();

            CollectionAssert.AreEqual(balances, await ReadBalancesAsync(db));
            var refunds = await db.Enrollments.Select(x => new { x.Status, x.RefundedVisits }).ToArrayAsync();
            Assert.AreEqual(seedRefunds.Length, refunds.Length);
            Assert.AreEqual(seedRefunds.Count(x => x.Status == "cancelled") + 1,
                refunds.Count(x => x.Status == "cancelled" && x.RefundedVisits == 0));
            Assert.IsTrue(refunds.All(x => x.RefundedVisits == (x.Status == "cancelled" ? 0 : (int?)null)));

            var invalidRefund = await Assert.ThrowsExceptionAsync<PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE enrollments SET refunded_visits = NULL WHERE id = {historicalId}
                    """));
            Assert.AreEqual(PostgresErrorCodes.CheckViolation, invalidRefund.SqlState);
            Assert.AreEqual("ck_enrollments_refunded_visits", invalidRefund.ConstraintName);

            var indexes = await db.Database.SqlQuery<string>($"""
                SELECT indexname AS "Value" FROM pg_indexes WHERE schemaname = 'public'
                """).ToArrayAsync();
            CollectionAssert.IsSubsetOf(new[]
            {
                "ix_classes_scheduled_start_id",
                "ix_classes_scheduled_room_start_id",
                "ix_enrollments_active_class_user"
            }, indexes);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static Task<int[]> ReadBalancesAsync(ClubDbContext db) =>
        db.Passes.OrderBy(x => x.Id).Select(x => x.RemainingVisits).ToArrayAsync();
}
