using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.Tests;

public partial class EnrollmentHttpContractTests
{
    [TestMethod]
    public async Task ConcurrentCancellationsRefundAndPromoteOnce()
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        await using var factory = new FixedTimeApplicationFactory(now);
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var owner = await AddBookingClientAsync(db, now.UtcDateTime, visits: 0);
        var candidate = await AddBookingClientAsync(db, now.UtcDateTime, visits: 4);
        var classId = await AddBookingClassAsync(db, now.UtcDateTime, capacity: 2);
        var booking = await AddEnrollmentAsync(db, owner, classId, now.UtcDateTime);
        var waiting = await AddWaitingAsync(db, candidate, classId, now.UtcDateTime, seats: 2);
        using var client = CreateAuthenticatedClient(factory, owner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var path = $"/enrollments/{booking.Id}/cancel";

        await using var gate = await db.Database.BeginTransactionAsync(ct);
        Assert.IsNotNull(await unitOfWork.Class.LockByIdAsync(classId, ct));
        var firstPending = client.PostAsync(path, null, ct);
        var secondPending = client.PostAsync(path, null, ct);
        await WaitForBlockedRequestsAsync(db, 2, ct);
        await gate.CommitAsync(ct);

        using var first = await firstPending;
        using var second = await secondPending;
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.AreEqual(2, body.GetProperty("refundedVisits").GetInt32());
        Assert.AreEqual(await first.Content.ReadAsStringAsync(ct), await second.Content.ReadAsStringAsync(ct));
        await db.Entry(booking).ReloadAsync(ct);
        Assert.AreEqual(("cancelled", 2, now.UtcDateTime), (booking.Status, booking.RefundedVisits, booking.CancelledAt));
        await AssertPersistedSeatsAndVisitsAsync(db, booking, seats: 2, visits: 2);
        await AssertPromotionAsync(db, waiting, WaitlistStatus.Promoted, visits: 2);
    }

    [TestMethod]
    [DataRow("create", true)]
    [DataRow("create", false)]
    [DataRow("increase", true)]
    [DataRow("increase", false)]
    [DataRow("shared pass", true)]
    [DataRow("shared pass", false)]
    public async Task CancellationPromotionCompetesWithOtherBookings(string operation, bool cancellationFirst)
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        await using var factory = new FixedTimeApplicationFactory(now);
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var sameClass = operation != "shared pass";
        var increase = operation == "increase";
        var owner = await AddBookingClientAsync(db, now.UtcDateTime, visits: 0);
        var candidate = await AddBookingClientAsync(db, now.UtcDateTime, visits: 3);
        var rival = sameClass ? await AddBookingClientAsync(db, now.UtcDateTime, visits: 1) : candidate;
        var classId = await AddBookingClassAsync(db, now.UtcDateTime, capacity: 5);
        var rivalClass = sameClass ? classId : await AddBookingClassAsync(db, now.UtcDateTime, capacity: 2);
        var booking = await AddEnrollmentAsync(db, owner, classId, now.UtcDateTime);
        var existing = await AddEnrollmentAsync(db, rival, classId, now.UtcDateTime);
        var waiting = await AddWaitingAsync(db, candidate, classId, now.UtcDateTime, seats: 3);
        using var ownerClient = CreateAuthenticatedClient(factory, owner);
        using var rivalClient = CreateAuthenticatedClient(factory, rival);
        using var createRequest = CreateRequest($$"""{"classId":{{rivalClass}},"seats":1}""", Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;

        await using var gate = await db.Database.BeginTransactionAsync(ct);
        if (sameClass)
            Assert.IsNotNull(await unitOfWork.Class.LockByIdAsync(classId, ct));
        else
            Assert.HasCount(1, await unitOfWork.Pass.LockByUsersAsync([candidate], ct));

        var first = cancellationFirst ? CancelAsync() : CompeteAsync();
        await WaitForBlockedRequestsAsync(db, 1, ct);
        var second = cancellationFirst ? CompeteAsync() : CancelAsync();
        await WaitForBlockedRequestsAsync(db, 2, ct);
        await gate.CommitAsync(ct);

        using var cancelled = await (cancellationFirst ? first : second);
        using var competing = await (cancellationFirst ? second : first);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
        await db.Entry(booking).ReloadAsync(ct);
        Assert.AreEqual(("cancelled", 2), (booking.Status, booking.RefundedVisits));
        await AssertPersistedSeatsAndVisitsAsync(db, booking, seats: 2, visits: 2);

        if (cancellationFirst)
        {
            Assert.AreEqual(HttpStatusCode.Conflict, competing.StatusCode);
            var problem = await competing.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.AreEqual(sameClass ? "capacity-exceeded" : "pass-exhausted", problem.GetProperty("reason").GetString());
        }
        else
        {
            Assert.AreEqual(increase ? HttpStatusCode.OK : HttpStatusCode.Created, competing.StatusCode);
        }

        var expectedStatus = cancellationFirst ? WaitlistStatus.Promoted
            : sameClass ? WaitlistStatus.Waiting : WaitlistStatus.Closed;
        var candidateVisits = cancellationFirst ? 0 : sameClass ? 3 : 2;
        await AssertPromotionAsync(db, waiting, expectedStatus, candidateVisits, existing.Id);
        if (sameClass)
            await AssertPersistedSeatsAndVisitsAsync(db, existing,
                seats: increase && !cancellationFirst ? 3 : 2, visits: cancellationFirst ? 1 : 0);

        var occupied = await db.Set<Enrollment>().Where(x => x.ClassId == classId && x.Status == "active")
            .SumAsync(x => x.Seats, ct);
        Assert.AreEqual(cancellationFirst ? 5 : sameClass ? 3 : 2, occupied);
        if (!sameClass)
            Assert.AreEqual(cancellationFirst ? 0 : 1,
                await db.Set<Enrollment>().CountAsync(x => x.ClassId == rivalClass && x.UserId == rival, ct));

        Task<HttpResponseMessage> CancelAsync() => ownerClient.PostAsync($"/enrollments/{booking.Id}/cancel", null, ct);
        Task<HttpResponseMessage> CompeteAsync() => increase
            ? rivalClient.PostAsJsonAsync($"/enrollments/{existing.Id}/seats", new { seats = 3 }, ct)
            : rivalClient.SendAsync(createRequest, ct);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationRejectsClassThatStartsWhileWaitingForLock(bool waitForCandidatePass)
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        await using var factory = new FixedTimeApplicationFactory(clock);
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var owner = await AddBookingClientAsync(db, now.UtcDateTime, visits: 0);
        var candidate = await AddBookingClientAsync(db, now.UtcDateTime, visits: 2);
        var classId = await AddBookingClassAsync(db, now.UtcDateTime, capacity: 2);
        var booking = await AddEnrollmentAsync(db, owner, classId, now.UtcDateTime);
        var waiting = await AddWaitingAsync(db, candidate, classId, now.UtcDateTime, seats: 2);
        using var client = CreateAuthenticatedClient(factory, owner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;

        await using var gate = await db.Database.BeginTransactionAsync(ct);
        if (waitForCandidatePass)
            Assert.HasCount(1, await unitOfWork.Pass.LockByUsersAsync([candidate], ct));
        else
            Assert.IsNotNull(await unitOfWork.Class.LockByIdAsync(classId, ct));

        var pending = client.PostAsync($"/enrollments/{booking.Id}/cancel", null, ct);
        await WaitForBlockedRequestsAsync(db, 1, ct);
        clock.SetUtcNow(now.AddDays(1));
        await gate.CommitAsync(ct);

        using var response = await pending;
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.AreEqual("class-started", problem.GetProperty("reason").GetString());
        await AssertPersistedSeatsAndVisitsAsync(db, booking, seats: 2, visits: 0);
        await AssertPromotionAsync(db, waiting, WaitlistStatus.Waiting, visits: 2);
    }

    [TestMethod]
    public async Task CancellationRejectsStartedClassBeforeWaitingForCandidatePass()
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        await using var factory = new FixedTimeApplicationFactory(now);
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var owner = await AddBookingClientAsync(db, now.UtcDateTime, visits: 0);
        var candidate = await AddBookingClientAsync(db, now.UtcDateTime, visits: 2);
        var classId = await AddBookingClassAsync(db, now.UtcDateTime.AddDays(-1), capacity: 2);
        var booking = await AddEnrollmentAsync(db, owner, classId, now.UtcDateTime);
        var waiting = await AddWaitingAsync(db, candidate, classId, now.UtcDateTime, seats: 2);
        using var client = CreateAuthenticatedClient(factory, owner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;

        await using var gate = await db.Database.BeginTransactionAsync(ct);
        Assert.HasCount(1, await unitOfWork.Pass.LockByUsersAsync([candidate], ct));
        using var response = await client.PostAsync($"/enrollments/{booking.Id}/cancel", null, ct);
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.AreEqual("class-started", problem.GetProperty("reason").GetString());
        await gate.CommitAsync(ct);

        await db.Entry(booking).ReloadAsync(ct);
        Assert.AreEqual("active", booking.Status);
        await AssertPersistedSeatsAndVisitsAsync(db, booking, seats: 2, visits: 0);
        await AssertPromotionAsync(db, waiting, WaitlistStatus.Waiting, visits: 2);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PromotionUsesRequestDateWhenCandidatePassLockWaitCrossesUtcMidnight(bool cancel)
    {
        var now = new DateTimeOffset(2026, 11, 16, 23, 59, 59, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        await using var factory = new FixedTimeApplicationFactory(clock);
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var owner = await AddBookingClientAsync(db, now.UtcDateTime, visits: 0);
        var candidate = await AddBookingClientAsync(db, now.UtcDateTime, visits: 1,
            validUntil: DateOnly.FromDateTime(now.UtcDateTime));
        var classId = await AddBookingClassAsync(db, now.UtcDateTime, capacity: 2);
        var booking = await AddEnrollmentAsync(db, owner, classId, now.UtcDateTime);
        var waiting = await AddWaitingAsync(db, candidate, classId, now.UtcDateTime, seats: 1);
        using var client = CreateAuthenticatedClient(factory, owner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;

        await using var gate = await db.Database.BeginTransactionAsync(ct);
        Assert.HasCount(1, await unitOfWork.Pass.LockByUsersAsync([candidate], ct));
        var pending = cancel
            ? client.PostAsync($"/enrollments/{booking.Id}/cancel", null, ct)
            : client.PostAsJsonAsync($"/enrollments/{booking.Id}/seats", new { seats = 1 }, ct);
        await WaitForBlockedRequestsAsync(db, 1, ct);
        var promotedAt = now.AddSeconds(1);
        clock.SetUtcNow(promotedAt);
        await gate.CommitAsync(ct);

        using var response = await pending;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await AssertPromotionAsync(db, waiting, WaitlistStatus.Promoted, visits: 0);
        var createdAt = await db.Set<Enrollment>().Where(x => x.ClassId == classId && x.UserId == candidate)
            .Select(x => x.CreatedAt).SingleAsync(ct);
        Assert.AreEqual(promotedAt.UtcDateTime, createdAt);
    }

    [TestMethod]
    public async Task CancellationRollsBackIfClassStartsWhileWritingChanges()
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        await using var factory = new FixedTimeApplicationFactory(clock);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var owner = await AddBookingClientAsync(db, now.UtcDateTime, visits: 0);
        var candidate = await AddBookingClientAsync(db, now.UtcDateTime, visits: 2);
        var classId = await AddBookingClassAsync(db, now.UtcDateTime, capacity: 2);
        var booking = await AddEnrollmentAsync(db, owner, classId, now.UtcDateTime);
        var waiting = await AddWaitingAsync(db, candidate, classId, now.UtcDateTime, seats: 2);
        using var client = CreateAuthenticatedClient(factory, owner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;

        await using var gate = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE enrollments IN SHARE MODE", ct);
        var pending = client.PostAsync($"/enrollments/{booking.Id}/cancel", null, ct);
        await WaitForBlockedRequestsAsync(db, 1, ct);
        clock.SetUtcNow(now.AddDays(1));
        await gate.CommitAsync(ct);

        using var response = await pending;
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.AreEqual("class-started", problem.GetProperty("reason").GetString());
        await db.Entry(booking).ReloadAsync(ct);
        Assert.AreEqual("active", booking.Status);
        Assert.IsNull(booking.RefundedVisits);
        Assert.IsNull(booking.CancelledAt);
        await AssertPersistedSeatsAndVisitsAsync(db, booking, seats: 2, visits: 0);
        await AssertPromotionAsync(db, waiting, WaitlistStatus.Waiting, visits: 2);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReleasingSeatsDoesNotWaitForPassesBeyondPromotionCandidates(bool cancel)
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        await using var factory = new FixedTimeApplicationFactory(now);
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var owner = await AddBookingClientAsync(db, now.UtcDateTime, visits: 0);
        var candidate = await AddBookingClientAsync(db, now.UtcDateTime, visits: 1);
        var oversizedUser = await AddBookingClientAsync(db, now.UtcDateTime, visits: 3);
        var tailUser = await AddBookingClientAsync(db, now.UtcDateTime, visits: 1);
        var classId = await AddBookingClassAsync(db, now.UtcDateTime, capacity: cancel ? 2 : 3);
        var booking = await AddEnrollmentAsync(db, owner, classId, now.UtcDateTime);
        var first = await AddWaitingAsync(db, candidate, classId, now.UtcDateTime, seats: 1);
        var oversized = await AddWaitingAsync(db, oversizedUser, classId, now.UtcDateTime, seats: 3);
        var tail = await AddWaitingAsync(db, tailUser, classId, now.UtcDateTime, seats: 1);
        using var client = CreateAuthenticatedClient(factory, owner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;

        await using var gate = await db.Database.BeginTransactionAsync(ct);
        Assert.HasCount(2, await unitOfWork.Pass.LockByUsersAsync([oversizedUser, tailUser], ct));
        using var response = cancel
            ? await client.PostAsync($"/enrollments/{booking.Id}/cancel", null, ct)
            : await client.PostAsJsonAsync($"/enrollments/{booking.Id}/seats", new { seats = 1 }, ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await gate.CommitAsync(ct);

        await db.Entry(booking).ReloadAsync(ct);
        Assert.AreEqual(cancel ? "cancelled" : "active", booking.Status);
        await AssertPersistedSeatsAndVisitsAsync(db, booking, seats: cancel ? 2 : 1, visits: cancel ? 2 : 1);
        await AssertPromotionAsync(db, first, WaitlistStatus.Promoted, visits: 0);
        await AssertPromotionAsync(db, oversized, WaitlistStatus.Waiting, visits: 3);
        await AssertPromotionAsync(db, tail, WaitlistStatus.Waiting, visits: 1);
    }

    [TestMethod]
    public async Task CancellationUsesRequestTimeForRefundWhenPassLockWaitCrossesTwoHourBoundary()
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        await using var factory = new FixedTimeApplicationFactory(clock);
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var owner = await AddBookingClientAsync(db, now.UtcDateTime, visits: 0);
        var classId = await AddBookingClassAsync(db, now.UtcDateTime.AddHours(2).AddDays(-1), capacity: 2);
        var booking = await AddEnrollmentAsync(db, owner, classId, now.UtcDateTime);
        using var client = CreateAuthenticatedClient(factory, owner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;

        await using var gate = await db.Database.BeginTransactionAsync(ct);
        Assert.HasCount(1, await unitOfWork.Pass.LockByUsersAsync([owner], ct));
        var pending = client.PostAsync($"/enrollments/{booking.Id}/cancel", null, ct);
        await WaitForBlockedRequestsAsync(db, 1, ct);
        var cancelledAt = now.AddSeconds(1);
        clock.SetUtcNow(cancelledAt);
        await gate.CommitAsync(ct);

        using var response = await pending;
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.AreEqual(2, body.GetProperty("refundedVisits").GetInt32());
        await db.Entry(booking).ReloadAsync(ct);
        Assert.AreEqual(("cancelled", 2, cancelledAt.UtcDateTime),
            (booking.Status, booking.RefundedVisits, booking.CancelledAt));
        await AssertPersistedSeatsAndVisitsAsync(db, booking, seats: 2, visits: 2);
    }

    [TestMethod]
    public async Task CancellationRollsBackAllWritesWhenPromotionFails()
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        var failure = new PromotionWriteFailure();
        await using var factory = new FixedTimeApplicationFactory(new AdjustableTimeProvider(now), failure);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var owner = await AddBookingClientAsync(db, now.UtcDateTime, visits: 0);
        var candidate = await AddBookingClientAsync(db, now.UtcDateTime, visits: 2);
        var exhausted = await AddBookingClientAsync(db, now.UtcDateTime, visits: 0);
        var classId = await AddBookingClassAsync(db, now.UtcDateTime, capacity: 2);
        var booking = await AddEnrollmentAsync(db, owner, classId, now.UtcDateTime);
        var closedOnSuccess = await AddWaitingAsync(db, exhausted, classId, now.UtcDateTime, seats: 1);
        var promotedOnSuccess = await AddWaitingAsync(db, candidate, classId, now.UtcDateTime, seats: 2);
        using var client = CreateAuthenticatedClient(factory, owner);
        var path = $"/enrollments/{booking.Id}/cancel";

        using var failed = await client.PostAsync(path, null);
        Assert.AreEqual(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.IsTrue(failure.WasTriggered);
        await AssertPersistedSeatsAndVisitsAsync(db, booking, seats: 2, visits: 0);
        await AssertPromotionAsync(db, closedOnSuccess, WaitlistStatus.Waiting, visits: 0);
        await AssertPromotionAsync(db, promotedOnSuccess, WaitlistStatus.Waiting, visits: 2);

        using var retried = await client.PostAsync(path, null);
        Assert.AreEqual(HttpStatusCode.OK, retried.StatusCode);
        await db.Entry(booking).ReloadAsync();
        Assert.AreEqual(("cancelled", 2), (booking.Status, booking.RefundedVisits));
        await AssertPersistedSeatsAndVisitsAsync(db, booking, seats: 2, visits: 2);
        await AssertPromotionAsync(db, closedOnSuccess, WaitlistStatus.Closed, visits: 0);
        await AssertPromotionAsync(db, promotedOnSuccess, WaitlistStatus.Promoted, visits: 0);
    }

    private static async Task<WaitlistEntry> AddWaitingAsync(
        DbContext db, long userId, long classId, DateTime createdAt, int seats)
    {
        var waiting = new WaitlistEntry { UserId = userId, ClassId = classId, Seats = seats, CreatedAt = createdAt };
        db.Add(waiting);
        await db.SaveChangesAsync();
        return waiting;
    }

    private static async Task AssertPromotionAsync(
        DbContext db, WaitlistEntry waiting, WaitlistStatus status, int visits, long? existingBookingId = null)
    {
        Assert.AreEqual(status, await db.Set<WaitlistEntry>().Where(x => x.Id == waiting.Id)
            .Select(x => x.Status).SingleAsync());
        Assert.AreEqual(visits, await db.Set<Pass>().Where(x => x.UserId == waiting.UserId)
            .Select(x => x.RemainingVisits).SingleAsync());
        var bookings = await db.Set<Enrollment>().AsNoTracking().Where(x =>
            x.ClassId == waiting.ClassId && x.UserId == waiting.UserId && x.Id != existingBookingId).ToArrayAsync();
        if (status == WaitlistStatus.Promoted)
        {
            var promoted = bookings.Single();
            Assert.AreEqual(("active", waiting.Seats), (promoted.Status, promoted.Seats));
        }
        else
        {
            Assert.HasCount(0, bookings);
        }
    }

    private sealed class PromotionWriteFailure : SaveChangesInterceptor
    {
        public bool WasTriggered { get; private set; }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!WasTriggered && eventData.Context!.ChangeTracker.Entries<WaitlistEntry>()
                .Any(x => x.Entity.Status == WaitlistStatus.Promoted))
            {
                WasTriggered = true;
                throw new InvalidOperationException("Simulated failure after saving waitlist promotion.");
            }

            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }
}
