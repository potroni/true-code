using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentDetailsQueryTests
{
    [TestMethod]
    [DataRow("active", false)]
    [DataRow("cancelled", false)]
    [DataRow("active", true)]
    public async Task GetDetailsReadsOwnedBookingWithCurrentPass(string status, bool classCancelled)
    {
        await using var factory = new ApiApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = (ClubDbContext)unitOfWork.DbContext;
        await using var transaction = await db.Database.BeginTransactionAsync();

        var pass = await db.Passes.OrderBy(x => x.Id).FirstAsync();
        var otherUserId = await db.Users.Where(x => x.Id != pass.UserId).Select(x => x.Id).FirstAsync();
        var room = await db.Rooms.OrderBy(x => x.Id).FirstAsync();
        var createdAt = new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc);
        var activity = new Class
        {
            RoomId = room.Id,
            Title = "Enrollment details test",
            StartsAt = createdAt.AddDays(1),
            DurationMinutes = 45,
            Capacity = 10,
            IsCancelled = classCancelled
        };
        var enrollment = new Enrollment
        {
            UserId = pass.UserId,
            Class = activity,
            Status = status,
            Seats = 2,
            CreatedAt = createdAt,
            RefundedVisits = status == "cancelled" ? 2 : null,
            CancelledAt = status == "cancelled" ? createdAt.AddHours(1) : null
        };
        db.Enrollments.Add(enrollment);
        await db.SaveChangesAsync();

        var expected = new EnrollmentDetails(
            enrollment.Id, status, 2, createdAt,
            activity.Id, activity.Title, activity.StartsAt, activity.DurationMinutes, classCancelled,
            room.Id, room.Name, pass.RemainingVisits, pass.ValidUntil);

        Assert.AreEqual(expected, await unitOfWork.Enrollment.GetDetailsAsync(enrollment.Id, pass.UserId));
        Assert.IsNull(await unitOfWork.Enrollment.GetDetailsAsync(enrollment.Id, otherUserId));
        Assert.IsNull(await unitOfWork.Enrollment.GetDetailsAsync(long.MaxValue, pass.UserId));

        var updatedBalance = pass.RemainingVisits + 1;
        await db.Passes.Where(x => x.Id == pass.Id)
            .ExecuteUpdateAsync(passes => passes.SetProperty(x => x.RemainingVisits, updatedBalance));

        Assert.AreEqual(expected with { RemainingVisits = updatedBalance },
            await unitOfWork.Enrollment.GetDetailsAsync(enrollment.Id, pass.UserId));
    }
}
