using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts;

namespace WebApi.Tests;

public partial class EnrollmentHttpContractTests
{
    [TestMethod]
    [DataRow(true, false, null)]
    [DataRow(false, false, "already-waiting")]
    [DataRow(true, true, "idempotency-key-conflict")]
    public async Task ConcurrentWaitlistJoinsPersistOneEntryAndJournal(
        bool sameKey, bool differentClasses, string? rejectionReason)
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        await using var factory = new FixedTimeApplicationFactory(now);
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var owner = await AddBookingClientAsync(db, now.UtcDateTime, visits: 0);
        var candidate = await AddBookingClientAsync(db, now.UtcDateTime, visits: 3);
        var firstClass = await AddBookingClassAsync(db, now.UtcDateTime, capacity: 2);
        await AddEnrollmentAsync(db, owner, firstClass, now.UtcDateTime);
        var secondClass = firstClass;
        if (differentClasses)
        {
            secondClass = await AddBookingClassAsync(db, now.UtcDateTime, capacity: 2);
            await AddEnrollmentAsync(db, owner, secondClass, now.UtcDateTime);
        }

        var firstKey = Guid.NewGuid().ToString("N");
        var secondKey = sameKey ? firstKey : Guid.NewGuid().ToString("N");
        using var client = CreateAuthenticatedClient(factory, candidate);
        using var firstRequest = CreateWaitlistJoin(firstClass, seats: 1, firstKey);
        using var secondRequest = CreateWaitlistJoin(secondClass, seats: 1, secondKey);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var gate = await db.Database.BeginTransactionAsync(ct);
        Assert.IsNotNull(await unitOfWork.Class.LockByIdAsync(firstClass, ct));

        var firstPending = client.SendAsync(firstRequest, ct);
        await WaitForBlockedRequestsAsync(db, 1, ct);
        var secondPending = client.SendAsync(secondRequest, ct);
        await WaitForBlockedRequestsAsync(db, 2, ct);
        await gate.CommitAsync(ct);

        using var first = await firstPending;
        using var second = await secondPending;
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        var originalBody = await first.Content.ReadAsStringAsync(ct);
        if (rejectionReason is null)
        {
            Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
            Assert.AreEqual(originalBody, await second.Content.ReadAsStringAsync(ct));
        }
        else
        {
            await AssertWaitlistConflictAsync(second, rejectionReason, ct);
        }

        var result = JsonSerializer.Deserialize<JoinWaitlistResponse>(originalBody, JsonSerializerOptions.Web);
        Assert.IsNotNull(result);
        var stored = await db.Set<WaitlistEntry>().SingleAsync(row => row.UserId == candidate, ct);
        Assert.AreEqual((result.Id, firstClass, 1, WaitlistStatus.Waiting),
            (stored.Id, stored.ClassId, stored.Seats, stored.Status));
        Assert.AreEqual(1, result.Position);
        var journal = await db.Set<IdempotencyEntry>().SingleAsync(row => row.UserId == candidate, ct);
        Assert.AreEqual((IdempotencyOperation.JoinWaitlist, firstKey, originalBody),
            (journal.Operation, journal.OriginalKey, journal.ResponseBody));
        await AssertPromotionAsync(db, stored, WaitlistStatus.Waiting, visits: 3);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task WaitlistJoinUsesSeatAvailabilityInClassLockOrder(bool bookingFirst)
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        await using var factory = new FixedTimeApplicationFactory(now);
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var owner = await AddBookingClientAsync(db, now.UtcDateTime, visits: 2);
        var candidate = await AddBookingClientAsync(db, now.UtcDateTime, visits: 2);
        var classId = await AddBookingClassAsync(db, now.UtcDateTime, capacity: 2);
        using var ownerClient = CreateAuthenticatedClient(factory, owner);
        using var candidateClient = CreateAuthenticatedClient(factory, candidate);
        using var bookingRequest = CreateRequest($$"""{"classId":{{classId}},"seats":2}""", Guid.NewGuid().ToString("N"));
        var joinKey = Guid.NewGuid().ToString("N");
        using var joinRequest = CreateWaitlistJoin(classId, seats: 1, joinKey);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var gate = await db.Database.BeginTransactionAsync(ct);
        Assert.IsNotNull(await unitOfWork.Class.LockByIdAsync(classId, ct));

        var firstPending = bookingFirst ? BookAsync() : JoinAsync();
        await WaitForBlockedRequestsAsync(db, 1, ct);
        var secondPending = bookingFirst ? JoinAsync() : BookAsync();
        await WaitForBlockedRequestsAsync(db, 2, ct);
        await gate.CommitAsync(ct);

        using var bookingResponse = await (bookingFirst ? firstPending : secondPending);
        using var joinResponse = await (bookingFirst ? secondPending : firstPending);
        Assert.AreEqual(HttpStatusCode.Created, bookingResponse.StatusCode);
        var booking = await db.Set<Enrollment>().SingleAsync(row => row.ClassId == classId, ct);
        Assert.AreEqual((owner, "active"), (booking.UserId, booking.Status));
        await AssertPersistedSeatsAndVisitsAsync(db, booking, seats: 2, visits: 0);
        if (bookingFirst)
        {
            Assert.AreEqual(HttpStatusCode.Created, joinResponse.StatusCode);
            var waitlisted = await db.Set<WaitlistEntry>().SingleAsync(row => row.ClassId == classId, ct);
            Assert.AreEqual((candidate, 1), (waitlisted.UserId, waitlisted.Seats));
            await AssertPromotionAsync(db, waitlisted, WaitlistStatus.Waiting, visits: 2);
        }
        else
        {
            await AssertWaitlistConflictAsync(joinResponse, "seats-available", ct);
            Assert.IsFalse(await db.Set<WaitlistEntry>().AnyAsync(row => row.ClassId == classId, ct));
            Assert.IsFalse(await db.Set<IdempotencyEntry>().AnyAsync(row => row.OriginalKey == joinKey, ct));
        }

        Task<HttpResponseMessage> BookAsync() => ownerClient.SendAsync(bookingRequest, ct);
        Task<HttpResponseMessage> JoinAsync() => candidateClient.SendAsync(joinRequest, ct);
    }

    [TestMethod]
    public async Task WaitlistJoinRejectsClassThatStartsWhileWaitingForLock()
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
        await AddEnrollmentAsync(db, owner, classId, now.UtcDateTime);
        using var client = CreateAuthenticatedClient(factory, candidate);
        var key = Guid.NewGuid().ToString("N");
        using var request = CreateWaitlistJoin(classId, seats: 1, key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var gate = await db.Database.BeginTransactionAsync(ct);
        Assert.IsNotNull(await unitOfWork.Class.LockByIdAsync(classId, ct));

        var pending = client.SendAsync(request, ct);
        await WaitForBlockedRequestsAsync(db, 1, ct);
        clock.SetUtcNow(now.AddDays(1));
        await gate.CommitAsync(ct);

        using var response = await pending;
        await AssertWaitlistConflictAsync(response, "class-started", ct);
        Assert.IsFalse(await db.Set<WaitlistEntry>().AnyAsync(row => row.ClassId == classId, ct));
        Assert.IsFalse(await db.Set<IdempotencyEntry>().AnyAsync(row => row.OriginalKey == key, ct));
    }

    [TestMethod]
    public async Task WaitlistJoinRollsBackWhenJournalSaveFailsAndSameKeyCanRetry()
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        var key = Guid.NewGuid().ToString("N");
        var failure = new WaitlistJournalSaveFailure(key);
        await using var factory = new FixedTimeApplicationFactory(new AdjustableTimeProvider(now), failure);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var owner = await AddBookingClientAsync(db, now.UtcDateTime, visits: 0);
        var candidate = await AddBookingClientAsync(db, now.UtcDateTime, visits: 2);
        var classId = await AddBookingClassAsync(db, now.UtcDateTime, capacity: 2);
        await AddEnrollmentAsync(db, owner, classId, now.UtcDateTime);
        using var client = CreateAuthenticatedClient(factory, candidate);

        using var firstRequest = CreateWaitlistJoin(classId, seats: 2, key);
        using var failed = await client.SendAsync(firstRequest);
        Assert.AreEqual(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.IsTrue(failure.WasTriggered);
        Assert.IsFalse(await db.Set<WaitlistEntry>().AnyAsync(row => row.ClassId == classId));
        Assert.IsFalse(await db.Set<IdempotencyEntry>().AnyAsync(row => row.OriginalKey == key));

        using var retryRequest = CreateWaitlistJoin(classId, seats: 2, key);
        using var retry = await client.SendAsync(retryRequest);
        Assert.AreEqual(HttpStatusCode.Created, retry.StatusCode);
        var body = await retry.Content.ReadAsStringAsync();
        var joined = JsonSerializer.Deserialize<JoinWaitlistResponse>(body, JsonSerializerOptions.Web);
        Assert.IsNotNull(joined);
        Assert.AreEqual(1, joined.Position);
        var waiting = await db.Set<WaitlistEntry>().SingleAsync(row => row.ClassId == classId);
        Assert.AreEqual((joined.Id, candidate, 2), (waiting.Id, waiting.UserId, waiting.Seats));
        await AssertPromotionAsync(db, waiting, WaitlistStatus.Waiting, visits: 2);
        var journal = await db.Set<IdempotencyEntry>().SingleAsync(row => row.OriginalKey == key);
        Assert.AreEqual(body, journal.ResponseBody);
    }

    [TestMethod]
    public async Task ConcurrentWaitlistExitsReturnTheSameResult()
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        await using var factory = new FixedTimeApplicationFactory(now);
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var candidate = await AddBookingClientAsync(db, now.UtcDateTime, visits: 2);
        var classId = await AddBookingClassAsync(db, now.UtcDateTime, capacity: 2);
        var entry = await AddWaitingAsync(db, candidate, classId, now.UtcDateTime, seats: 2);
        using var client = CreateAuthenticatedClient(factory, candidate);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var path = $"{WaitlistPath}/{entry.Id}/cancel";
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
        Assert.AreEqual(await first.Content.ReadAsStringAsync(ct), await second.Content.ReadAsStringAsync(ct));
        var result = await first.Content.ReadFromJsonAsync<CancelWaitlistResponse>(ct);
        Assert.IsNotNull(result);
        Assert.AreEqual((entry.Id, "cancelled"), (result.Id, result.Status));
        await AssertPromotionAsync(db, entry, WaitlistStatus.Cancelled, visits: 2);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task WaitlistExitCompetesWithPromotion(bool exitFirst)
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        await using var factory = new FixedTimeApplicationFactory(now);
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var owner = await AddBookingClientAsync(db, now.UtcDateTime, visits: 0);
        var candidate = await AddBookingClientAsync(db, now.UtcDateTime, visits: 4);
        var next = await AddBookingClientAsync(db, now.UtcDateTime, visits: 4);
        var classId = await AddBookingClassAsync(db, now.UtcDateTime, capacity: 2);
        var booking = await AddEnrollmentAsync(db, owner, classId, now.UtcDateTime);
        var entry = await AddWaitingAsync(db, candidate, classId, now.UtcDateTime, seats: 2);
        var following = await AddWaitingAsync(db, next, classId, now.UtcDateTime.AddSeconds(1), seats: 2);
        using var ownerClient = CreateAuthenticatedClient(factory, owner);
        using var candidateClient = CreateAuthenticatedClient(factory, candidate);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var gate = await db.Database.BeginTransactionAsync(ct);
        Assert.IsNotNull(await unitOfWork.Class.LockByIdAsync(classId, ct));

        var firstPending = exitFirst ? ExitAsync() : CancelBookingAsync();
        await WaitForBlockedRequestsAsync(db, 1, ct);
        var secondPending = exitFirst ? CancelBookingAsync() : ExitAsync();
        await WaitForBlockedRequestsAsync(db, 2, ct);
        await gate.CommitAsync(ct);

        using var exit = await (exitFirst ? firstPending : secondPending);
        using var cancellation = await (exitFirst ? secondPending : firstPending);
        Assert.AreEqual(HttpStatusCode.OK, cancellation.StatusCode);
        if (exitFirst)
        {
            Assert.AreEqual(HttpStatusCode.OK, exit.StatusCode);
            await AssertPromotionAsync(db, entry, WaitlistStatus.Cancelled, visits: 4);
            await AssertPromotionAsync(db, following, WaitlistStatus.Promoted, visits: 2);
        }
        else
        {
            await AssertWaitlistConflictAsync(exit, "already-promoted", ct);
            await AssertPromotionAsync(db, entry, WaitlistStatus.Promoted, visits: 2);
            await AssertPromotionAsync(db, following, WaitlistStatus.Waiting, visits: 4);
        }

        await db.Entry(booking).ReloadAsync(ct);
        Assert.AreEqual(("cancelled", 2), (booking.Status, booking.RefundedVisits));
        await AssertPersistedSeatsAndVisitsAsync(db, booking, seats: 2, visits: 2);

        Task<HttpResponseMessage> ExitAsync() =>
            candidateClient.PostAsync($"{WaitlistPath}/{entry.Id}/cancel", null, ct);
        Task<HttpResponseMessage> CancelBookingAsync() =>
            ownerClient.PostAsync($"/enrollments/{booking.Id}/cancel", null, ct);
    }

    private static HttpRequestMessage CreateWaitlistJoin(long classId, int seats, string key) =>
        CreateRequest($$"""{"classId":{{classId}},"seats":{{seats}}}""", key, WaitlistPath);

    private static async Task AssertWaitlistConflictAsync(
        HttpResponseMessage response, string reason, CancellationToken ct)
    {
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.AreEqual(reason, problem.GetProperty("reason").GetString());
    }

    private sealed class WaitlistJournalSaveFailure(string key) : SaveChangesInterceptor
    {
        public bool WasTriggered { get; private set; }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!WasTriggered && eventData.Context!.ChangeTracker.Entries<IdempotencyEntry>()
                .Any(entry => entry.Entity.Operation == IdempotencyOperation.JoinWaitlist
                    && entry.Entity.OriginalKey == key))
            {
                WasTriggered = true;
                throw new InvalidOperationException("Simulated failure after saving the waitlist journal.");
            }

            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }
}
