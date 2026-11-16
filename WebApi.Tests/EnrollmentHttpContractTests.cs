using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebApi.Auth;
using WebApi.Contracts;
using WebApi.Services;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentHttpContractTests
{
    private const long UserId = 17;
    private const string ValidBody = """{"classId":42,"seats":2,"userId":99}""";

    [TestMethod]
    [DataRow("active", false)]
    [DataRow("cancelled", true)]
    public async Task GetDetailsReturnsTheOwnedBookingWithCurrentPass(string status, bool classCancelled)
    {
        await using var factory = new ApiApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var now = new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc);
        var validUntil = DateOnly.FromDateTime(now).AddDays(1);
        var userId = await AddBookingClientAsync(db, now, visits: 5, validUntil: validUntil);
        var otherUserId = await AddBookingClientAsync(db, now, visits: 5);
        var classId = await AddBookingClassAsync(db, now, capacity: 10);
        var activity = await db.Set<Class>().Include(x => x.Room).SingleAsync(x => x.Id == classId);
        activity.IsCancelled = classCancelled;
        var enrollment = new Enrollment
        {
            UserId = userId,
            ClassId = classId,
            Seats = 2,
            Status = status,
            CreatedAt = now,
            CancelledAt = status == "cancelled" ? now.AddHours(1) : null,
            RefundedVisits = status == "cancelled" ? 2 : null
        };
        db.Add(enrollment);
        await db.SaveChangesAsync();

        using var client = CreateAuthenticatedClient(factory, userId);
        using var response = await client.GetAsync($"/enrollments/{enrollment.Id}?userId={otherUserId}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(enrollment.Id, body.GetProperty("id").GetInt64());
        Assert.AreEqual(status, body.GetProperty("status").GetString());
        Assert.AreEqual(2, body.GetProperty("seats").GetInt32());
        var createdAt = body.GetProperty("createdAt").GetDateTimeOffset();
        Assert.AreEqual(new DateTimeOffset(now), createdAt);
        Assert.AreEqual(TimeSpan.Zero, createdAt.Offset);
        var classBody = body.GetProperty("class");
        Assert.AreEqual(classId, classBody.GetProperty("id").GetInt64());
        Assert.AreEqual(activity.Title, classBody.GetProperty("title").GetString());
        var startsAt = classBody.GetProperty("startsAt").GetDateTimeOffset();
        Assert.AreEqual(new DateTimeOffset(activity.StartsAt), startsAt);
        Assert.AreEqual(TimeSpan.Zero, startsAt.Offset);
        Assert.AreEqual(activity.DurationMinutes, classBody.GetProperty("durationMinutes").GetInt32());
        Assert.AreEqual(classCancelled, classBody.GetProperty("isCancelled").GetBoolean());
        var room = classBody.GetProperty("room");
        Assert.AreEqual(activity.Room.Id, room.GetProperty("id").GetInt64());
        Assert.AreEqual(activity.Room.Name, room.GetProperty("name").GetString());
        var pass = body.GetProperty("pass");
        Assert.AreEqual(5, pass.GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual("2026-11-17", pass.GetProperty("validUntil").GetString());

        using var otherClient = CreateAuthenticatedClient(factory, otherUserId);
        using var foreign = await otherClient.GetAsync($"/enrollments/{enrollment.Id}?userId={userId}");
        using var missing = await otherClient.GetAsync($"/enrollments/{long.MaxValue}");
        foreach (var notFound in new[] { foreign, missing })
        {
            Assert.AreEqual(HttpStatusCode.NotFound, notFound.StatusCode);
            Assert.AreEqual("application/problem+json", notFound.Content.Headers.ContentType?.MediaType);
            var problem = await notFound.Content.ReadFromJsonAsync<ProblemDetails>();
            Assert.IsNotNull(problem);
            Assert.AreEqual(404, problem.Status);
            Assert.IsNull(problem.Detail);
        }
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("invalid")]
    [DataRow("9223372036854775808")]
    public async Task GetDetailsRejectsInvalidIdsBeforeCallingTheService(string id)
    {
        var service = new EnrollmentServiceStub();
        using var factory = new EnrollmentApplicationFactory(service);
        using var client = CreateAuthenticatedClient(factory);
        using var response = await client.GetAsync($"/enrollments/{id}");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.IsNull(service.DetailsArguments);
    }

    [TestMethod]
    public async Task GetDetailsRequiresJwt()
    {
        using var factory = new EnrollmentApplicationFactory(new EnrollmentServiceStub());
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/enrollments/1");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task GetDetailsDocumentsItsContractInOpenApi()
    {
        using var factory = new EnrollmentApplicationFactory(new EnrollmentServiceStub());
        using var client = factory.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var operation = document.GetProperty("paths").GetProperty("/enrollments/{id}").GetProperty("get");
        var id = operation.GetProperty("parameters")[0];
        Assert.AreEqual("id", id.GetProperty("name").GetString());
        Assert.AreEqual("path", id.GetProperty("in").GetString());
        Assert.IsTrue(id.GetProperty("required").GetBoolean());
        Assert.AreEqual("int64", id.GetProperty("schema").GetProperty("format").GetString());

        var responses = operation.GetProperty("responses");
        CollectionAssert.AreEquivalent(new[] { "200", "400", "401", "404", "429" },
            responses.EnumerateObject().Select(response => response.Name).ToArray());
        var schema = responses.GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        Assert.AreEqual("#/components/schemas/EnrollmentDetailsResponse", schema.GetProperty("$ref").GetString());
    }

    [TestMethod]
    public async Task CreateUsesJwtIdentityAndReturnsTheCreatedContract()
    {
        var service = new EnrollmentServiceStub();
        using var factory = new EnrollmentApplicationFactory(service);
        using var client = CreateAuthenticatedClient(factory);
        using var request = CreateRequest(ValidBody, "booking-123");
        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual((UserId, 42L, 2, "booking-123"), service.Arguments);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.HasCount(5, body.EnumerateObject().ToArray());
        Assert.AreEqual(123L, body.GetProperty("id").GetInt64());
        Assert.AreEqual(42L, body.GetProperty("classId").GetInt64());
        Assert.AreEqual(2, body.GetProperty("seats").GetInt32());
        Assert.AreEqual("active", body.GetProperty("status").GetString());
        var createdAt = body.GetProperty("createdAt").GetDateTimeOffset();
        Assert.AreEqual(EnrollmentServiceStub.CreatedAt, createdAt);
        Assert.AreEqual(TimeSpan.Zero, createdAt.Offset);
    }

    [TestMethod]
    [DataRow(ValidBody, null)]
    [DataRow(ValidBody, "")]
    [DataRow(ValidBody, "   ")]
    [DataRow(null, "key")]
    [DataRow("{", "key")]
    [DataRow("""{"seats":2}""", "key")]
    [DataRow("""{"classId":42}""", "key")]
    [DataRow("""{"classId":42,"seats":0}""", "key")]
    [DataRow("""{"classId":"42","seats":2}""", "key")]
    public async Task CreateRejectsInvalidInputBeforeCallingTheService(string? body, string? key)
    {
        var service = new EnrollmentServiceStub();
        using var factory = new EnrollmentApplicationFactory(service);
        using var client = CreateAuthenticatedClient(factory);
        using var request = CreateRequest(body, key);
        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.IsNull(service.Arguments);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("invalid-token")]
    public async Task CreateRequiresAValidJwt(string? token)
    {
        var service = new EnrollmentServiceStub();
        using var factory = new EnrollmentApplicationFactory(service);
        using var client = factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var request = CreateRequest(ValidBody, "key");
        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsNull(service.Arguments);
    }

    [TestMethod]
    public async Task CreateUsesTheBusinessRateLimit()
    {
        using var factory = new EnrollmentApplicationFactory(new EnrollmentServiceStub());
        using var client = CreateAuthenticatedClient(factory);

        for (var index = 0; index < 60; index++)
        {
            using var request = CreateRequest(ValidBody, "key");
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        }

        using var rejectedRequest = CreateRequest(ValidBody, "key");
        using var rejected = await client.SendAsync(rejectedRequest);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [TestMethod]
    public async Task GetDetailsSharesTheBusinessRateLimitWithCreate()
    {
        using var factory = new EnrollmentApplicationFactory(new EnrollmentServiceStub());
        using var client = CreateAuthenticatedClient(factory);

        for (var index = 0; index < 60; index++)
        {
            using var response = await client.GetAsync("/enrollments/1");
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var request = CreateRequest(ValidBody, "key");
        using var rejected = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        using var rejectedGet = await client.GetAsync("/enrollments/1");
        Assert.AreEqual(HttpStatusCode.TooManyRequests, rejectedGet.StatusCode);
    }

    [TestMethod]
    [DataRow("missing-class", 2, HttpStatusCode.NotFound, null)]
    [DataRow("cancelled-class", 2, HttpStatusCode.Conflict, "class-cancelled")]
    [DataRow("started-class", 2, HttpStatusCode.Conflict, "class-started")]
    [DataRow("available", 3, HttpStatusCode.Conflict, "capacity-exceeded")]
    [DataRow("exhausted-pass", 2, HttpStatusCode.Conflict, "pass-exhausted")]
    [DataRow("expired-pass", 2, HttpStatusCode.Conflict, "pass-expired")]
    [DataRow("available", 2, HttpStatusCode.Created, null)]
    [DataRow("journal-replay", 2, HttpStatusCode.Created, null)]
    [DataRow("journal-conflict", 2, HttpStatusCode.Conflict, "idempotency-key-conflict")]
    public async Task CreateAppliesAvailabilityAndIdempotencyRulesAtomically(
        string scenario, int seats, HttpStatusCode expectedStatus, string? expectedReason)
    {
        var storedTime = new DateTimeOffset(2026, 11, 15, 23, 59, 59, TimeSpan.Zero).AddTicks(1234560);
        var now = scenario == "started-class" ? storedTime : storedTime.AddTicks(7);
        await using var factory = new FixedTimeApplicationFactory(now);
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var roomId = await db.Set<Room>().Select(room => room.Id).FirstAsync();
        var user = new User
        {
            Email = $"booking-{Guid.NewGuid():N}@club.test",
            PasswordHash = "unused",
            Role = "user",
            CreatedAt = now.UtcDateTime
        };
        var activity = new Class
        {
            RoomId = roomId,
            Title = "Availability test",
            StartsAt = scenario == "started-class" ? now.UtcDateTime : now.UtcDateTime.AddMonths(1),
            DurationMinutes = 60,
            Capacity = 4,
            IsCancelled = scenario == "cancelled-class"
        };
        db.AddRange(user, activity);
        await db.SaveChangesAsync();

        var remainingVisits = scenario == "exhausted-pass" ? 1 : 2;
        db.AddRange(
            new Pass
            {
                UserId = user.Id,
                RemainingVisits = remainingVisits,
                ValidUntil = DateOnly.FromDateTime(now.UtcDateTime)
                    .AddDays(scenario == "expired-pass" ? -1 : 0)
            },
            new Enrollment
            {
                ClassId = activity.Id, UserId = user.Id, Seats = 2,
                Status = "active", CreatedAt = now.UtcDateTime
            },
            new Enrollment
            {
                ClassId = activity.Id, UserId = user.Id, Seats = 2,
                Status = "cancelled", CreatedAt = now.UtcDateTime,
                CancelledAt = now.UtcDateTime, RefundedVisits = 2
            },
            new WaitlistEntry
            {
                ClassId = activity.Id, UserId = user.Id, Seats = 3, CreatedAt = now.UtcDateTime
            });
        await db.SaveChangesAsync();

        var classId = scenario == "missing-class" ? long.MaxValue : activity.Id;
        var key = Guid.NewGuid().ToString("N");
        using var client = CreateAuthenticatedClient(factory, user.Id);
        using var request = CreateRequest($$"""{"classId":{{classId}},"seats":{{seats}}}""", key);
        var winnerSeats = scenario == "journal-conflict" ? seats - 1 : seats;
        var winnerBody = $$"""{"id":123,"classId":{{classId}},"seats":{{winnerSeats}},"status":"active","createdAt":"2026-11-15T08:00:00Z"}""";
        var simulateIndexConflict = scenario is "journal-replay" or "journal-conflict";
        using var response = simulateIndexConflict
            ? await SendWithRacingJournalInsertAsync(unitOfWork, client, request, user.Id, classId, key,
                winnerSeats, winnerBody)
            : await client.SendAsync(request);

        Assert.AreEqual(expectedStatus, response.StatusCode);
        Assert.AreEqual(expectedStatus == HttpStatusCode.Created ? "application/json" : "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        if (expectedReason is not null)
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(expectedReason, problem.GetProperty("reason").GetString());
        }

        var created = scenario == "available" && expectedStatus == HttpStatusCode.Created;
        Assert.AreEqual(created ? 0 : remainingVisits, await db.Set<Pass>().Where(pass => pass.UserId == user.Id)
            .Select(pass => pass.RemainingVisits).SingleAsync());
        Assert.AreEqual(created ? 3 : 2, await db.Set<Enrollment>()
            .CountAsync(enrollment => enrollment.UserId == user.Id));
        Assert.AreEqual(created ? 4L : 2L, await unitOfWork.Enrollment.SumActiveSeatsAsync(activity.Id));
        var journalBody = await db.Set<IdempotencyEntry>().Where(entry => entry.OriginalKey == key)
            .Select(entry => entry.ResponseBody).SingleOrDefaultAsync();

        if (created)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(body, journalBody);
            var enrollment = await db.Set<Enrollment>().AsNoTracking()
                .Where(entry => entry.UserId == user.Id && entry.Status == "active")
                .OrderByDescending(entry => entry.Id).FirstAsync();
            Assert.AreEqual(storedTime.UtcDateTime, enrollment.CreatedAt);
            var expected = new EnrollmentResponse(enrollment.Id, classId, seats, "active", storedTime);
            Assert.AreEqual(expected, JsonSerializer.Deserialize<EnrollmentResponse>(body, JsonSerializerOptions.Web));

            using var replay = CreateRequest($$"""{"classId":{{classId}},"seats":{{seats}}}""", key);
            using var replayResponse = await client.SendAsync(replay);
            Assert.AreEqual(HttpStatusCode.Created, replayResponse.StatusCode);
            Assert.AreEqual(body, await replayResponse.Content.ReadAsStringAsync());
        }
        else if (simulateIndexConflict)
        {
            Assert.AreEqual(winnerBody, journalBody);
            if (expectedStatus == HttpStatusCode.Created)
                Assert.AreEqual(winnerBody, await response.Content.ReadAsStringAsync());
        }
        else
        {
            Assert.IsNull(journalBody);
        }
    }

    private static async Task<HttpResponseMessage> SendWithRacingJournalInsertAsync(
        IUnitOfWork unitOfWork, HttpClient client, HttpRequestMessage request,
        long userId, long classId, string key, int winnerSeats, string winnerBody)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var db = unitOfWork.DbContext;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        Assert.IsNotNull(await unitOfWork.Class.LockByIdAsync(classId, ct));
        var pendingResponse = client.SendAsync(request, ct);
        try
        {
            while (!await db.Database.SqlQueryRaw<bool>(
                """
                SELECT EXISTS (
                    SELECT 1 FROM pg_locks
                    WHERE NOT granted AND pg_backend_pid() = ANY(pg_blocking_pids(pid))
                ) AS "Value"
                """).SingleAsync(ct))
            {
                Assert.IsFalse(pendingResponse.IsCompleted, "The HTTP request did not wait for the class lock.");
                await Task.Delay(20, ct);
            }

            unitOfWork.Idempotency.AddSuccess(
                userId, IdempotencyOperation.CreateEnrollment, key, classId, winnerSeats, winnerBody);
            await unitOfWork.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return await pendingResponse;
        }
        finally
        {
            if (!pendingResponse.IsCompleted)
                await timeout.CancelAsync();
        }
    }

    [TestMethod]
    public async Task CreateReplaysStoredJsonAndRejectsChangedParameters()
    {
        await using var factory = new ApiApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var userId = await unitOfWork.DbContext.Set<User>().Select(user => user.Id).FirstAsync();
        var classId = await unitOfWork.DbContext.Set<Class>().Select(activity => activity.Id).FirstAsync();
        var key = Guid.NewGuid().ToString("N");
        var storedBody =
            $$"""{"status":"active", "id":123, "classId":{{classId}}, "seats":2, "createdAt":"2026-11-15T08:00:00Z"}""";

        await using (var transaction = await unitOfWork.DbContext.Database.BeginTransactionAsync())
        {
            unitOfWork.Idempotency.AddSuccess(
                userId, IdempotencyOperation.CreateEnrollment, key, classId, 2, storedBody);
            await unitOfWork.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        using var client = CreateAuthenticatedClient(factory, userId);
        using var replay = CreateRequest($$"""{"classId":{{classId}},"seats":2}""", key);
        using var replayResponse = await client.SendAsync(replay);
        Assert.AreEqual(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.AreEqual("application/json", replayResponse.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(storedBody, await replayResponse.Content.ReadAsStringAsync());

        foreach (var body in new[]
        {
            $$"""{"classId":{{classId}},"seats":1}""",
            """{"classId":9223372036854775807,"seats":2}"""
        })
        {
            using var conflict = CreateRequest(body, key);
            using var conflictResponse = await client.SendAsync(conflict);
            Assert.AreEqual(HttpStatusCode.Conflict, conflictResponse.StatusCode);
            Assert.AreEqual("application/problem+json", conflictResponse.Content.Headers.ContentType?.MediaType);
            var problem = await conflictResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual("idempotency-key-conflict", problem.GetProperty("reason").GetString());
        }

        Assert.AreEqual(1, await unitOfWork.DbContext.Set<IdempotencyEntry>()
            .CountAsync(entry => entry.OriginalKey == key));
    }

    [TestMethod]
    public async Task CreateWaitsForKeyLockAndReplaysResponseAfterCommit()
    {
        await using var factory = new ApiApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var userId = await db.Set<User>().Select(user => user.Id).FirstAsync();
        var classId = await db.Set<Class>().Select(activity => activity.Id).FirstAsync();
        var key = Guid.NewGuid().ToString("N");
        var storedBody =
            $$"""{"status":"active", "id":123, "classId":{{classId}}, "seats":2, "createdAt":"2026-11-15T08:00:00Z"}""";
        using var client = CreateAuthenticatedClient(factory, userId);
        using var request = CreateRequest($$"""{"classId":{{classId}},"seats":2}""", key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        const IdempotencyOperation operation = IdempotencyOperation.CreateEnrollment;
        await unitOfWork.Idempotency.LockAsync(userId, operation, key, ct);
        unitOfWork.Idempotency.AddSuccess(userId, operation, key, classId, 2, storedBody);
        await unitOfWork.SaveChangesAsync(ct);

        var pendingResponse = client.SendAsync(request, ct);
        try
        {
            while (!await db.Database.SqlQueryRaw<bool>(
                """
                SELECT EXISTS (
                    SELECT 1 FROM pg_locks
                    WHERE locktype = 'advisory' AND NOT granted
                        AND pg_backend_pid() = ANY(pg_blocking_pids(pid))
                ) AS "Value"
                """).SingleAsync(ct))
            {
                Assert.IsFalse(pendingResponse.IsCompleted, "The HTTP request did not wait for the key lock.");
                await Task.Delay(20, ct);
            }

            await transaction.CommitAsync(ct);
            using var response = await pendingResponse;
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            Assert.AreEqual(storedBody, await response.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            if (!pendingResponse.IsCompleted)
                await timeout.CancelAsync();
        }
    }

    [TestMethod]
    public async Task CreateReplaysOriginalResponseAfterResizeAndCancellation()
    {
        await using var factory = new ApiApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var now = DateTime.UtcNow;
        var userId = await AddBookingClientAsync(db, now, visits: 3);
        var classId = await AddBookingClassAsync(db, now, capacity: 3);
        var key = Guid.NewGuid().ToString("N");
        var body = $$"""{"classId":{{classId}},"seats":1}""";
        using var client = CreateAuthenticatedClient(factory, userId);
        using var request = CreateRequest(body, key);
        using var created = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var original = await created.Content.ReadAsByteArrayAsync();
        var booking = JsonSerializer.Deserialize<EnrollmentResponse>(original, JsonSerializerOptions.Web);
        Assert.IsNotNull(booking);

        var enrollment = await db.Set<Enrollment>().SingleAsync(entry => entry.Id == booking.Id);
        var pass = await db.Set<Pass>().SingleAsync(entry => entry.UserId == userId);
        await db.Entry(pass).ReloadAsync();
        enrollment.Seats = 2;
        pass.RemainingVisits -= 1;
        await db.SaveChangesAsync();
        await AssertReplayAsync(expectedVisits: 1);

        enrollment.Status = "cancelled";
        enrollment.CancelledAt = now.AddMinutes(1);
        enrollment.RefundedVisits = 2;
        pass.RemainingVisits += 2;
        await db.SaveChangesAsync();
        await AssertReplayAsync(expectedVisits: 3);

        async Task AssertReplayAsync(int expectedVisits)
        {
            using var replay = CreateRequest(body, key);
            using var response = await client.SendAsync(replay);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            CollectionAssert.AreEqual(original, await response.Content.ReadAsByteArrayAsync());

            var persisted = await db.Set<Enrollment>().AsNoTracking().SingleAsync(entry => entry.UserId == userId);
            Assert.AreEqual((booking.Id, enrollment.Seats, enrollment.Status, enrollment.RefundedVisits),
                (persisted.Id, persisted.Seats, persisted.Status, persisted.RefundedVisits));
            Assert.AreEqual(expectedVisits, await db.Set<Pass>().Where(entry => entry.UserId == userId)
                .Select(entry => entry.RemainingVisits).SingleAsync());
            Assert.AreEqual(1, await db.Set<IdempotencyEntry>().CountAsync(entry => entry.UserId == userId));
        }
    }

    [TestMethod]
    [DataRow("class")]
    [DataRow("pass")]
    [DataRow("occupied seats")]
    public async Task CreateRejectsClassThatStartsWhileCheckingAvailability(string blockedRead)
    {
        var now = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(now);
        await using var factory = new FixedTimeApplicationFactory(clock);
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var userId = await AddBookingClientAsync(db, now.UtcDateTime, visits: 1);
        var classId = await AddBookingClassAsync(db, now.UtcDateTime, capacity: 1);
        var key = Guid.NewGuid().ToString("N");
        using var client = CreateAuthenticatedClient(factory, userId);
        using var request = CreateRequest($$"""{"classId":{{classId}},"seats":1}""", key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;

        await using var gate = await db.Database.BeginTransactionAsync(ct);
        if (blockedRead == "pass")
            Assert.HasCount(1, await unitOfWork.Pass.LockByUsersAsync([userId], ct));
        else if (blockedRead == "occupied seats")
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE enrollments IN ACCESS EXCLUSIVE MODE", ct);
        else
            Assert.IsNotNull(await unitOfWork.Class.LockByIdAsync(classId, ct));

        var pendingResponse = client.SendAsync(request, ct);
        try
        {
            await WaitForBlockedRequestsAsync(db, 1, ct);
            clock.SetUtcNow(now.AddDays(1));
            await gate.CommitAsync(ct);

            using var response = await pendingResponse;
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.AreEqual("class-started", problem.GetProperty("reason").GetString());
            Assert.IsFalse(await db.Set<Enrollment>().AnyAsync(entry => entry.ClassId == classId, ct));
            Assert.IsFalse(await db.Set<IdempotencyEntry>().AnyAsync(entry => entry.OriginalKey == key, ct));
            Assert.AreEqual(1, await db.Set<Pass>().Where(pass => pass.UserId == userId)
                .Select(pass => pass.RemainingVisits).SingleAsync(ct));
        }
        finally
        {
            if (!pendingResponse.IsCompleted)
                await timeout.CancelAsync();
        }
    }

    [TestMethod]
    public async Task CreateUsesRequestDateWhenPassLockWaitCrossesUtcMidnight()
    {
        var requestedAt = new DateTimeOffset(2026, 11, 15, 23, 59, 59, TimeSpan.Zero);
        var clock = new AdjustableTimeProvider(requestedAt);
        await using var factory = new FixedTimeApplicationFactory(clock);
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var userId = await AddBookingClientAsync(db, requestedAt.UtcDateTime, visits: 1,
            validUntil: DateOnly.FromDateTime(requestedAt.UtcDateTime));
        var classId = await AddBookingClassAsync(db, requestedAt.UtcDateTime, capacity: 1);
        var key = Guid.NewGuid().ToString("N");
        using var client = CreateAuthenticatedClient(factory, userId);
        using var request = CreateRequest($$"""{"classId":{{classId}},"seats":1}""", key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;

        await using var gate = await db.Database.BeginTransactionAsync(ct);
        Assert.HasCount(1, await unitOfWork.Pass.LockByUsersAsync([userId], ct));
        var pendingResponse = client.SendAsync(request, ct);
        try
        {
            await WaitForBlockedRequestsAsync(db, 1, ct);
            clock.SetUtcNow(requestedAt.AddSeconds(1));
            await gate.CommitAsync(ct);

            using var response = await pendingResponse;
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            var enrollment = await db.Set<Enrollment>().SingleAsync(entry => entry.ClassId == classId, ct);
            Assert.AreEqual((userId, 1, "active"), (enrollment.UserId, enrollment.Seats, enrollment.Status));
            Assert.AreEqual(0, await db.Set<Pass>().Where(pass => pass.UserId == userId)
                .Select(pass => pass.RemainingVisits).SingleAsync(ct));
            var journalBody = await db.Set<IdempotencyEntry>().Where(entry => entry.OriginalKey == key)
                .Select(entry => entry.ResponseBody).SingleAsync(ct);
            Assert.AreEqual(await response.Content.ReadAsStringAsync(ct), journalBody);
        }
        finally
        {
            if (!pendingResponse.IsCompleted)
                await timeout.CancelAsync();
        }
    }

    [TestMethod]
    public async Task CreateAllowsDifferentUsersToReuseTheSameKey()
    {
        await using var factory = new ApiApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var now = DateTime.UtcNow;
        var firstUser = await AddBookingClientAsync(db, now, visits: 1);
        var secondUser = await AddBookingClientAsync(db, now, visits: 1);
        var classId = await AddBookingClassAsync(db, now, capacity: 2);
        var key = Guid.NewGuid().ToString("N");
        var body = $$"""{"classId":{{classId}},"seats":1}""";

        foreach (var userId in new[] { firstUser, secondUser })
        {
            using var client = CreateAuthenticatedClient(factory, userId);
            using var request = CreateRequest(body, key);
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            var responseBody = await response.Content.ReadAsStringAsync();
            var booking = JsonSerializer.Deserialize<EnrollmentResponse>(responseBody, JsonSerializerOptions.Web);
            Assert.IsNotNull(booking);
            var enrolledUser = await db.Set<Enrollment>().Where(entry => entry.Id == booking.Id)
                .Select(entry => entry.UserId).SingleAsync();
            Assert.AreEqual(userId, enrolledUser);
            Assert.AreEqual(0, await db.Set<Pass>().Where(pass => pass.UserId == userId)
                .Select(pass => pass.RemainingVisits).SingleAsync());

            using var replay = CreateRequest(body, key);
            using var replayResponse = await client.SendAsync(replay);
            Assert.AreEqual(HttpStatusCode.Created, replayResponse.StatusCode);
            Assert.AreEqual(responseBody, await replayResponse.Content.ReadAsStringAsync());
        }

        Assert.AreEqual(2, await db.Set<Enrollment>().CountAsync(entry => entry.ClassId == classId));
        Assert.AreEqual(2, await db.Set<IdempotencyEntry>().CountAsync(entry => entry.OriginalKey == key));
    }

    [TestMethod]
    [DataRow(true, false, false, null)]
    [DataRow(true, true, false, "idempotency-key-conflict")]
    [DataRow(false, false, true, "capacity-exceeded")]
    [DataRow(false, true, false, "pass-exhausted")]
    public async Task ConcurrentCreatesCommitOnlyOneBooking(
        bool sameKey, bool differentClasses, bool differentUsers, string? rejectionReason)
    {
        await using var factory = new ApiApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var now = DateTime.UtcNow;
        const int seats = 1;
        var firstUser = await AddBookingClientAsync(db, now, seats);
        var secondUser = differentUsers ? await AddBookingClientAsync(db, now, seats) : firstUser;
        var classCapacity = differentClasses ? seats + 1 : seats;
        var firstClass = await AddBookingClassAsync(db, now, classCapacity);
        var secondClass = differentClasses ? await AddBookingClassAsync(db, now, classCapacity) : firstClass;
        var firstKey = Guid.NewGuid().ToString("N");
        var secondKey = sameKey ? firstKey : Guid.NewGuid().ToString("N");
        using var firstClient = CreateAuthenticatedClient(factory, firstUser);
        using var secondClient = CreateAuthenticatedClient(factory, secondUser);
        using var firstRequest = CreateRequest($$"""{"classId":{{firstClass}},"seats":{{seats}}}""", firstKey);
        using var secondRequest = CreateRequest($$"""{"classId":{{secondClass}},"seats":{{seats}}}""", secondKey);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;

        await using var gate = await db.Database.BeginTransactionAsync(ct);
        if (!sameKey && differentClasses)
            Assert.HasCount(1, await unitOfWork.Pass.LockByUsersAsync([firstUser], ct));
        else
            Assert.IsNotNull(await unitOfWork.Class.LockByIdAsync(firstClass, ct));

        var firstPending = firstClient.SendAsync(firstRequest, ct);
        try
        {
            await WaitForBlockedRequestsAsync(db, 1, ct);
            var secondPending = secondClient.SendAsync(secondRequest, ct);
            await WaitForBlockedRequestsAsync(db, 2, ct);
            await gate.CommitAsync(ct);

            using var first = await firstPending;
            using var second = await secondPending;
            var firstWon = first.StatusCode == HttpStatusCode.Created;
            var winner = firstWon ? first : second;
            var loser = firstWon ? second : first;
            Assert.AreEqual(HttpStatusCode.Created, winner.StatusCode);
            var winnerBody = await winner.Content.ReadAsStringAsync(ct);
            if (rejectionReason is null)
            {
                Assert.AreEqual(HttpStatusCode.Created, loser.StatusCode);
                Assert.AreEqual(winnerBody, await loser.Content.ReadAsStringAsync(ct));
            }
            else
            {
                Assert.AreEqual(HttpStatusCode.Conflict, loser.StatusCode);
                var problem = await loser.Content.ReadFromJsonAsync<JsonElement>(ct);
                Assert.AreEqual(rejectionReason, problem.GetProperty("reason").GetString());
            }

            var winnerUser = firstWon ? firstUser : secondUser;
            var winnerClass = firstWon ? firstClass : secondClass;
            var winnerKey = firstWon ? firstKey : secondKey;
            var persisted = await db.Set<Enrollment>().SingleAsync(
                entry => entry.ClassId == firstClass || entry.ClassId == secondClass, ct);
            Assert.AreEqual((winnerUser, winnerClass, seats, "active"),
                (persisted.UserId, persisted.ClassId, persisted.Seats, persisted.Status));
            var enrollment = JsonSerializer.Deserialize<EnrollmentResponse>(winnerBody, JsonSerializerOptions.Web);
            Assert.IsNotNull(enrollment);
            Assert.AreEqual(persisted.Id, enrollment.Id);
            var journal = await db.Set<IdempotencyEntry>().SingleAsync(
                entry => entry.OriginalKey == firstKey || entry.OriginalKey == secondKey, ct);
            Assert.AreEqual((winnerUser, winnerClass, seats, winnerKey, winnerBody),
                (journal.UserId, journal.ClassId, journal.Seats, journal.OriginalKey, journal.ResponseBody));
            var balances = await db.Set<Pass>().AsNoTracking()
                .Where(pass => pass.UserId == firstUser || pass.UserId == secondUser)
                .ToDictionaryAsync(pass => pass.UserId, pass => pass.RemainingVisits, ct);
            Assert.AreEqual(0, balances[winnerUser]);
            if (differentUsers)
                Assert.AreEqual(seats, balances[firstWon ? secondUser : firstUser]);
        }
        finally
        {
            await timeout.CancelAsync();
        }
    }

    private static async Task WaitForBlockedRequestsAsync(DbContext db, int expected, CancellationToken ct)
    {
        while (await db.Database.SqlQueryRaw<int>(
            """
            WITH RECURSIVE blocked(pid) AS (
                SELECT pg_backend_pid()
                UNION
                SELECT waiting.pid
                FROM blocked
                JOIN pg_locks AS waiting ON NOT waiting.granted
                    AND blocked.pid = ANY(pg_blocking_pids(waiting.pid))
            )
            SELECT COUNT(*)::int - 1 AS "Value" FROM blocked
            """).SingleAsync(ct) < expected)
        {
            await Task.Delay(20, ct);
        }
    }

    private static async Task<long> AddBookingClientAsync(
        DbContext db, DateTime now, int visits, DateOnly? validUntil = null)
    {
        var user = new User
        {
            Email = $"booking-{Guid.NewGuid():N}@club.test",
            PasswordHash = "unused",
            Role = "user",
            CreatedAt = now
        };
        db.Add(user);
        await db.SaveChangesAsync();
        db.Add(new Pass
        {
            UserId = user.Id,
            RemainingVisits = visits,
            ValidUntil = validUntil ?? DateOnly.FromDateTime(now).AddDays(1)
        });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<long> AddBookingClassAsync(DbContext db, DateTime now, int capacity)
    {
        var activity = new Class
        {
            RoomId = await db.Set<Room>().Select(room => room.Id).FirstAsync(),
            Title = "Booking test",
            StartsAt = now.AddDays(1),
            DurationMinutes = 60,
            Capacity = capacity
        };
        db.Add(activity);
        await db.SaveChangesAsync();
        return activity.Id;
    }

    private static HttpClient CreateAuthenticatedClient(ApiApplicationFactory factory, long userId = UserId)
    {
        var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<AccessTokenService>()
            .Create(new User { Id = userId, Email = "client@club.test", Role = "user" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private static HttpRequestMessage CreateRequest(string? body, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments?userId=99");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return request;
    }

    private sealed class EnrollmentApplicationFactory(EnrollmentServiceStub service) : ApiApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IEnrollmentService>(service)));
        }
    }

    private sealed class FixedTimeApplicationFactory(TimeProvider clock) : ApiApplicationFactory
    {
        public FixedTimeApplicationFactory(DateTimeOffset now) : this(new AdjustableTimeProvider(now)) { }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton(clock)));
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long _utcTicks = now.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void SetUtcNow(DateTimeOffset value) => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);
    }

    private sealed class EnrollmentServiceStub : IEnrollmentService
    {
        public static readonly DateTimeOffset CreatedAt = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);

        public (long UserId, long ClassId, int Seats, string Key)? Arguments { get; private set; }

        public (long EnrollmentId, long UserId)? DetailsArguments { get; private set; }

        public Task<EnrollmentDetailsResponse?> GetDetailsAsync(
            long enrollmentId, long userId, CancellationToken ct)
        {
            DetailsArguments = (enrollmentId, userId);
            return Task.FromResult<EnrollmentDetailsResponse?>(null);
        }

        public Task<CreateEnrollmentResult> CreateAsync(
            long userId, long classId, int seats, string idempotencyKey, CancellationToken ct)
        {
            Arguments = (userId, classId, seats, idempotencyKey);
            var response = new EnrollmentResponse(123, classId, seats, "active", CreatedAt);
            var body = JsonSerializer.Serialize(response, JsonSerializerOptions.Web);
            return Task.FromResult<CreateEnrollmentResult>(new CreateEnrollmentResult.Success(body));
        }
    }
}
