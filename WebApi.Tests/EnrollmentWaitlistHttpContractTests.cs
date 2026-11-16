using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts;
using WebApi.Services;

namespace WebApi.Tests;

public partial class EnrollmentHttpContractTests
{
    private const string WaitlistPath = "/enrollments/waitlist";

    [TestMethod]
    public async Task CancellingBookingThroughApiPromotesFirstWaitlistedClientAndDebitsOnce()
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        await using var factory = new FixedTimeApplicationFactory(now);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var owner = await AddBookingClientAsync(db, now.UtcDateTime, visits: 2);
        var firstUser = await AddBookingClientAsync(db, now.UtcDateTime, visits: 4);
        var secondUser = await AddBookingClientAsync(db, now.UtcDateTime, visits: 4);
        var classId = await AddBookingClassAsync(db, now.UtcDateTime, capacity: 2);
        using var ownerClient = CreateAuthenticatedClient(factory, owner);
        using var firstClient = CreateAuthenticatedClient(factory, firstUser);
        using var secondClient = CreateAuthenticatedClient(factory, secondUser);

        using var bookingRequest = CreateRequest($$"""{"classId":{{classId}},"seats":2}""", Guid.NewGuid().ToString("N"));
        using var bookingResponse = await ownerClient.SendAsync(bookingRequest);
        Assert.AreEqual(HttpStatusCode.Created, bookingResponse.StatusCode);
        var booking = await bookingResponse.Content.ReadFromJsonAsync<EnrollmentResponse>();
        Assert.IsNotNull(booking);
        var firstKey = Guid.NewGuid().ToString("N");
        using var firstJoinRequest = CreateWaitlistJoin(classId, seats: 2, firstKey);
        using var firstJoin = await firstClient.SendAsync(firstJoinRequest);
        Assert.AreEqual(HttpStatusCode.Created, firstJoin.StatusCode);
        var firstBody = await firstJoin.Content.ReadAsStringAsync();
        var first = JsonSerializer.Deserialize<JoinWaitlistResponse>(firstBody, JsonSerializerOptions.Web);
        Assert.IsNotNull(first);
        Assert.AreEqual(1, first.Position);
        using var secondJoinRequest = CreateWaitlistJoin(classId, seats: 1, Guid.NewGuid().ToString("N"));
        using var secondJoin = await secondClient.SendAsync(secondJoinRequest);
        Assert.AreEqual(HttpStatusCode.Created, secondJoin.StatusCode);
        var second = await secondJoin.Content.ReadFromJsonAsync<JoinWaitlistResponse>();
        Assert.IsNotNull(second);
        Assert.AreEqual(2, second.Position);

        var entries = await db.Set<WaitlistEntry>().AsNoTracking().Where(row => row.ClassId == classId)
            .ToDictionaryAsync(row => row.Id);
        await AssertPromotionAsync(db, entries[first.Id], WaitlistStatus.Waiting, visits: 4);
        await AssertPromotionAsync(db, entries[second.Id], WaitlistStatus.Waiting, visits: 4);

        var cancelPath = $"/enrollments/{booking.Id}/cancel";
        using var cancellation = await ownerClient.PostAsync(cancelPath, null);
        Assert.AreEqual(HttpStatusCode.OK, cancellation.StatusCode);
        var cancelled = await cancellation.Content.ReadFromJsonAsync<CancelEnrollmentResponse>();
        Assert.IsNotNull(cancelled);
        Assert.AreEqual((booking.Id, "cancelled", 2), (cancelled.Id, cancelled.Status, cancelled.RefundedVisits));
        var active = await db.Set<Enrollment>().SingleAsync(row => row.ClassId == classId && row.Status == "active");
        Assert.AreEqual(firstUser, active.UserId);
        using var detailsResponse = await firstClient.GetAsync($"/enrollments/{active.Id}");
        Assert.AreEqual(HttpStatusCode.OK, detailsResponse.StatusCode);
        var details = await detailsResponse.Content.ReadFromJsonAsync<EnrollmentDetailsResponse>();
        Assert.IsNotNull(details);
        Assert.AreEqual((active.Id, classId, 2, "active", 2),
            (details.Id, details.Class.Id, details.Seats, details.Status, details.Pass.RemainingVisits));

        using var repeatCancellation = await ownerClient.PostAsync(cancelPath, null);
        Assert.AreEqual(HttpStatusCode.OK, repeatCancellation.StatusCode);
        Assert.AreEqual(await cancellation.Content.ReadAsStringAsync(),
            await repeatCancellation.Content.ReadAsStringAsync());

        using var replayRequest = CreateWaitlistJoin(classId, seats: 2, firstKey);
        using var replay = await firstClient.SendAsync(replayRequest);
        Assert.AreEqual(HttpStatusCode.Created, replay.StatusCode);
        Assert.AreEqual(firstBody, await replay.Content.ReadAsStringAsync());

        Assert.AreEqual(2, await db.Set<Pass>().Where(row => row.UserId == owner)
            .Select(row => row.RemainingVisits).SingleAsync());
        await AssertPromotionAsync(db, entries[first.Id], WaitlistStatus.Promoted, visits: 2);
        await AssertPromotionAsync(db, entries[second.Id], WaitlistStatus.Waiting, visits: 4);
    }

    [TestMethod]
    public async Task CancelWaitlistUsesJwtIdentityAndRequiresNeitherBodyNorKey()
    {
        var service = new EnrollmentServiceStub
        {
            WaitlistCancellationResult = new CancelWaitlistResult.Success(new CancelWaitlistResponse(long.MaxValue))
        };
        using var factory = new EnrollmentApplicationFactory(service);
        using var authenticated = CreateAuthenticatedClient(factory);
        using var anonymous = factory.CreateClient();
        var path = $"{WaitlistPath}/{long.MaxValue}/cancel?userId=99";
        using var unauthorized = await anonymous.PostAsync(path, null);
        using var invalidId = await authenticated.PostAsync($"{WaitlistPath}/invalid/cancel", null);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidId.StatusCode);
        Assert.IsNull(service.WaitlistCancellationArguments);

        using var response = await authenticated.PostAsync(path, null);
        Assert.AreEqual((long.MaxValue, UserId), service.WaitlistCancellationArguments);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertProperties(body, "id", "status");
        Assert.AreEqual(long.MaxValue, body.GetProperty("id").GetInt64());
        Assert.AreEqual("cancelled", body.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task CancelWaitlistDocumentsItsContractInOpenApi()
    {
        using var factory = new EnrollmentApplicationFactory(new EnrollmentServiceStub());
        using var client = factory.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var operation = document.GetProperty("paths").GetProperty(WaitlistPath + "/{id}/cancel")
            .GetProperty("post");
        Assert.IsFalse(operation.TryGetProperty("requestBody", out _));
        var parameters = operation.GetProperty("parameters");
        Assert.AreEqual(1, parameters.GetArrayLength());
        Assert.AreEqual("id", parameters[0].GetProperty("name").GetString());
        Assert.AreEqual("path", parameters[0].GetProperty("in").GetString());
        AssertProperties(operation.GetProperty("responses"), "200", "400", "401", "404", "409", "429");
        var properties = document.GetProperty("components").GetProperty("schemas")
            .GetProperty("CancelWaitlistResponse").GetProperty("properties");
        AssertProperties(properties, "id", "status");
    }

    [TestMethod]
    [DataRow(WaitlistStatus.Waiting, null)]
    [DataRow(WaitlistStatus.Promoted, "already-promoted")]
    [DataRow(WaitlistStatus.Closed, "waitlist-closed")]
    public async Task CancelWaitlistRespectsOwnershipAndPreservesFinalStates(WaitlistStatus status, string? reason)
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        await using var factory = new FixedTimeApplicationFactory(now);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var users = await db.Set<User>().OrderBy(user => user.Id).Select(user => user.Id).Take(2).ToArrayAsync();
        var classId = await AddBookingClassAsync(db, now.UtcDateTime, capacity: 2);
        var entry = new WaitlistEntry
        {
            ClassId = classId, UserId = users[0], Seats = 1, CreatedAt = now.UtcDateTime, Status = status
        };
        var next = new WaitlistEntry
        {
            ClassId = classId, UserId = users[1], Seats = 1, CreatedAt = now.UtcDateTime.AddSeconds(1),
            Status = WaitlistStatus.Waiting
        };
        db.AddRange(entry, next);
        await db.SaveChangesAsync();

        using var owner = CreateAuthenticatedClient(factory, users[0]);
        using var other = CreateAuthenticatedClient(factory, users[1]);
        var path = $"{WaitlistPath}/{entry.Id}/cancel";
        using var foreign = await other.PostAsync(path, null);
        using var missing = await other.PostAsync($"{WaitlistPath}/0/cancel", null);
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.AreEqual(missing.StatusCode, foreign.StatusCode);

        using var response = await owner.PostAsync(path, null);
        if (reason is null)
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var repeat = await owner.PostAsync(path, null);
            Assert.AreEqual(HttpStatusCode.OK, repeat.StatusCode);
            Assert.AreEqual(await response.Content.ReadAsStringAsync(), await repeat.Content.ReadAsStringAsync());
        }
        else
        {
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(reason, problem.GetProperty("reason").GetString());
        }

        var stored = await db.Set<WaitlistEntry>().AsNoTracking().SingleAsync(row => row.Id == entry.Id);
        Assert.AreEqual(status == WaitlistStatus.Waiting ? WaitlistStatus.Cancelled : status, stored.Status);
        var following = await db.Set<WaitlistEntry>().AsNoTracking().SingleAsync(row => row.Id == next.Id);
        Assert.AreEqual(WaitlistStatus.Waiting, following.Status);
        Assert.IsFalse(await db.Set<Enrollment>().AnyAsync(booking => booking.ClassId == classId));
    }

    [TestMethod]
    public async Task JoinWaitlistUsesJwtIdentityAndReturnsTheWaitlistFields()
    {
        var createdAt = EnrollmentServiceStub.CreatedAt;
        var service = new EnrollmentServiceStub
        {
            WaitlistResult = new JoinWaitlistResult.Success(JsonSerializer.Serialize(
                new JoinWaitlistResponse(123, long.MaxValue, 2, 1, createdAt), JsonSerializerOptions.Web))
        };
        using var factory = new EnrollmentApplicationFactory(service);
        using var client = CreateAuthenticatedClient(factory);
        using var request = CreateRequest("""{"classId":9223372036854775807,"seats":2,"userId":99}""",
            "waitlist-key", WaitlistPath + "?userId=99");
        using var response = await client.SendAsync(request);

        Assert.AreEqual((UserId, long.MaxValue, 2, "waitlist-key"), service.WaitlistArguments);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertProperties(body, "id", "classId", "seats", "position", "createdAt");
        Assert.AreEqual(123L, body.GetProperty("id").GetInt64());
        Assert.AreEqual(long.MaxValue, body.GetProperty("classId").GetInt64());
        Assert.AreEqual(2, body.GetProperty("seats").GetInt32());
        Assert.AreEqual(1, body.GetProperty("position").GetInt32());
        var actualCreatedAt = body.GetProperty("createdAt").GetDateTimeOffset();
        Assert.AreEqual(createdAt, actualCreatedAt);
        Assert.AreEqual(TimeSpan.Zero, actualCreatedAt.Offset);
    }

    [TestMethod]
    [DataRow(ValidBody, null)]
    [DataRow(ValidBody, "")]
    [DataRow(ValidBody, "   ")]
    [DataRow(null, "key")]
    [DataRow("null", "key")]
    [DataRow("{", "key")]
    [DataRow("""{"seats":1}""", "key")]
    [DataRow("""{"classId":42}""", "key")]
    [DataRow("""{"classId":0,"seats":1}""", "key")]
    [DataRow("""{"classId":-1,"seats":1}""", "key")]
    [DataRow("""{"classId":"42","seats":1}""", "key")]
    [DataRow("""{"classId":9223372036854775808,"seats":1}""", "key")]
    [DataRow("""{"classId":42,"seats":0}""", "key")]
    [DataRow("""{"classId":42,"seats":1.5}""", "key")]
    [DataRow("""{"classId":42,"seats":"1"}""", "key")]
    [DataRow("""{"classId":42,"seats":2147483648}""", "key")]
    public async Task JoinWaitlistRejectsInvalidInputBeforeCallingTheService(string? body, string? key)
    {
        var service = new EnrollmentServiceStub();
        using var factory = new EnrollmentApplicationFactory(service);
        using var client = CreateAuthenticatedClient(factory);
        using var request = CreateRequest(body, key, WaitlistPath);
        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.IsNull(service.WaitlistArguments);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("invalid-token")]
    public async Task JoinWaitlistRequiresAValidJwt(string? token)
    {
        var service = new EnrollmentServiceStub();
        using var factory = new EnrollmentApplicationFactory(service);
        using var client = factory.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var request = CreateRequest(ValidBody, "key", WaitlistPath);
        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsNull(service.WaitlistArguments);
    }

    [TestMethod]
    [DataRow(null, HttpStatusCode.NotFound)]
    [DataRow("class-cancelled", HttpStatusCode.Conflict)]
    [DataRow("class-started", HttpStatusCode.Conflict)]
    [DataRow("already-waiting", HttpStatusCode.Conflict)]
    [DataRow("seats-exceed-capacity", HttpStatusCode.Conflict)]
    [DataRow("seats-available", HttpStatusCode.Conflict)]
    public async Task JoinWaitlistMapsServiceErrorsToProblemDetails(string? reason, HttpStatusCode status)
    {
        var service = new EnrollmentServiceStub
        {
            WaitlistResult = reason is null
                ? new JoinWaitlistResult.ClassNotFound()
                : new JoinWaitlistResult.Conflict(reason)
        };
        using var factory = new EnrollmentApplicationFactory(service);
        using var client = CreateAuthenticatedClient(factory);
        using var request = CreateRequest(ValidBody, "key", WaitlistPath);
        using var response = await client.SendAsync(request);

        Assert.AreEqual(status, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual((int)status, body.GetProperty("status").GetInt32());
        if (reason is not null)
            Assert.AreEqual(reason, body.GetProperty("reason").GetString());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WaitlistRoutesShareTheBusinessRateLimit(bool cancel)
    {
        using var factory = new EnrollmentApplicationFactory(new EnrollmentServiceStub
        {
            WaitlistResult = new JoinWaitlistResult.ClassNotFound()
        });
        using var client = CreateAuthenticatedClient(factory);
        for (var index = 0; index < 20; index++)
        {
            using var details = await client.GetAsync("/enrollments/1");
            Assert.AreEqual(HttpStatusCode.NotFound, details.StatusCode);
            using var request = CreateRequest(ValidBody, "key", WaitlistPath);
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            using var cancellation = await client.PostAsync($"{WaitlistPath}/1/cancel", null);
            Assert.AreEqual(HttpStatusCode.NotFound, cancellation.StatusCode);
        }

        using var rejectedRequest = cancel
            ? new HttpRequestMessage(HttpMethod.Post, $"{WaitlistPath}/1/cancel")
            : CreateRequest(ValidBody, "key", WaitlistPath);
        using var rejected = await client.SendAsync(rejectedRequest);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.AreEqual("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task JoinWaitlistDocumentsItsContractInOpenApi()
    {
        using var factory = new EnrollmentApplicationFactory(new EnrollmentServiceStub());
        using var client = factory.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var operation = document.GetProperty("paths").GetProperty(WaitlistPath).GetProperty("post");
        var key = operation.GetProperty("parameters")[0];
        Assert.AreEqual("Idempotency-Key", key.GetProperty("name").GetString());
        Assert.AreEqual("header", key.GetProperty("in").GetString());
        Assert.IsTrue(key.GetProperty("required").GetBoolean());
        Assert.IsTrue(operation.GetProperty("requestBody").GetProperty("required").GetBoolean());

        var responses = operation.GetProperty("responses");
        AssertProperties(responses, "201", "400", "401", "404", "409", "429");
        var schema = responses.GetProperty("201").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        Assert.AreEqual("#/components/schemas/JoinWaitlistResponse", schema.GetProperty("$ref").GetString());
        var properties = document.GetProperty("components").GetProperty("schemas")
            .GetProperty("JoinWaitlistResponse").GetProperty("properties");
        AssertProperties(properties, "id", "classId", "seats", "position", "createdAt");
        Assert.AreEqual("date-time", properties.GetProperty("createdAt").GetProperty("format").GetString());
    }

    [TestMethod]
    public async Task JoinWaitlistCreatesAnEntryInFifoOrderAndReplaysItsOriginalPosition()
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero).AddTicks(9);
        var storedTime = now.UtcDateTime.AddTicks(-9);
        await using var factory = new FixedTimeApplicationFactory(now);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var users = await db.Set<User>().OrderBy(user => user.Id).Select(user => user.Id).Take(2).ToArrayAsync();
        var classId = await AddBookingClassAsync(db, storedTime, capacity: 2);
        var first = new WaitlistEntry
        {
            ClassId = classId, UserId = users[1], Seats = 1, CreatedAt = storedTime, Status = WaitlistStatus.Waiting
        };
        db.AddRange(
            new Enrollment
            {
                ClassId = classId, UserId = users[0], Seats = 2, CreatedAt = storedTime, Status = "active"
            },
            first,
            new WaitlistEntry
            {
                ClassId = classId, UserId = users[0], Seats = 1, CreatedAt = storedTime.AddMinutes(-1),
                Status = WaitlistStatus.Cancelled
            },
            new WaitlistEntry
            {
                ClassId = classId, UserId = users[0], Seats = 1, CreatedAt = storedTime.AddMinutes(-2),
                Status = WaitlistStatus.Promoted
            },
            new WaitlistEntry
            {
                ClassId = classId, UserId = users[0], Seats = 1, CreatedAt = storedTime.AddMinutes(-3),
                Status = WaitlistStatus.Closed
            });
        await db.SaveChangesAsync();
        var visits = await db.Set<Pass>().Where(pass => pass.UserId == users[0])
            .Select(pass => pass.RemainingVisits).SingleAsync();
        var key = Guid.NewGuid().ToString("N");
        var requestBody = $$"""{"classId":{{classId}},"seats":1}""";
        using var client = CreateAuthenticatedClient(factory, users[0]);
        using var request = CreateRequest(requestBody, key, WaitlistPath);
        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JoinWaitlistResponse>(body, JsonSerializerOptions.Web)!;
        var entry = await db.Set<WaitlistEntry>().AsNoTracking().SingleAsync(row => row.Id == result.Id);
        Assert.AreEqual((classId, 1, 2, new DateTimeOffset(storedTime)),
            (result.ClassId, result.Seats, result.Position, result.CreatedAt));
        Assert.AreEqual((classId, users[0], 1, storedTime, WaitlistStatus.Waiting),
            (entry.ClassId, entry.UserId, entry.Seats, entry.CreatedAt, entry.Status));
        Assert.AreEqual(1, await db.Set<Enrollment>().CountAsync(booking => booking.ClassId == classId));
        Assert.AreEqual(visits, await db.Set<Pass>().Where(pass => pass.UserId == users[0])
            .Select(pass => pass.RemainingVisits).SingleAsync());

        using var duplicate = CreateRequest(requestBody, Guid.NewGuid().ToString("N"), WaitlistPath);
        using var duplicateResponse = await client.SendAsync(duplicate);
        Assert.AreEqual(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        var problem = await duplicateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("already-waiting", problem.GetProperty("reason").GetString());

        using var firstClient = CreateAuthenticatedClient(factory, users[1]);
        using var cancelFirst = await firstClient.PostAsync($"{WaitlistPath}/{first.Id}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, cancelFirst.StatusCode);
        using var replay = CreateRequest(requestBody, key, WaitlistPath);
        using var replayResponse = await client.SendAsync(replay);
        Assert.AreEqual(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.AreEqual(body, await replayResponse.Content.ReadAsStringAsync());

        using var rejoin = CreateRequest(requestBody, Guid.NewGuid().ToString("N"), WaitlistPath);
        using var rejoinResponse = await firstClient.SendAsync(rejoin);
        Assert.AreEqual(HttpStatusCode.Created, rejoinResponse.StatusCode);
        var rejoined = await rejoinResponse.Content.ReadFromJsonAsync<JoinWaitlistResponse>();
        Assert.IsNotNull(rejoined);
        Assert.AreNotEqual(first.Id, rejoined.Id);
        Assert.AreEqual(2, rejoined.Position);

        using var cancellation = await client.PostAsync($"{WaitlistPath}/{result.Id}/cancel", null);
        Assert.AreEqual(HttpStatusCode.OK, cancellation.StatusCode);
        using var replayAfterCancellation = CreateRequest(requestBody, key, WaitlistPath);
        using var responseAfterCancellation = await client.SendAsync(replayAfterCancellation);
        Assert.AreEqual(HttpStatusCode.Created, responseAfterCancellation.StatusCode);
        Assert.AreEqual(body, await responseAfterCancellation.Content.ReadAsStringAsync());
        Assert.AreEqual(WaitlistStatus.Cancelled, await db.Set<WaitlistEntry>().Where(row => row.Id == result.Id)
            .Select(row => row.Status).SingleAsync());
    }

    [TestMethod]
    [DataRow("missing", 1, HttpStatusCode.NotFound, null)]
    [DataRow("cancelled", 3, HttpStatusCode.Conflict, "class-cancelled")]
    [DataRow("started", 3, HttpStatusCode.Conflict, "class-started")]
    [DataRow("upcoming", 3, HttpStatusCode.Conflict, "seats-exceed-capacity")]
    [DataRow("upcoming", 1, HttpStatusCode.Conflict, "seats-available")]
    public async Task JoinWaitlistRejectsUnavailableRequests(string state, int seats, HttpStatusCode status, string? reason)
    {
        var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        await using var factory = new FixedTimeApplicationFactory(now);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var userId = await db.Set<User>().Select(user => user.Id).FirstAsync();
        var classId = long.MaxValue;
        if (state != "missing")
        {
            classId = await AddBookingClassAsync(db, now.UtcDateTime, capacity: 2);
            var activity = await db.Set<Class>().SingleAsync(row => row.Id == classId);
            if (state == "started")
                activity.StartsAt = now.UtcDateTime;
            activity.IsCancelled = state == "cancelled";
            db.Add(new Enrollment
            {
                ClassId = classId, UserId = userId,
                Seats = 1, CreatedAt = now.UtcDateTime, Status = "active"
            });
            await db.SaveChangesAsync();
        }
        var key = Guid.NewGuid().ToString("N");
        using var client = CreateAuthenticatedClient(factory, userId);
        using var request = CreateRequest($$"""{"classId":{{classId}},"seats":{{seats}}}""", key, WaitlistPath);
        using var response = await client.SendAsync(request);

        Assert.AreEqual(status, response.StatusCode);
        if (reason is not null)
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(reason, problem.GetProperty("reason").GetString());
        }
        Assert.IsFalse(await db.Set<WaitlistEntry>().AnyAsync(entry => entry.ClassId == classId));
        Assert.IsFalse(await db.Set<IdempotencyEntry>().AnyAsync(entry => entry.OriginalKey == key));
    }

    [TestMethod]
    public async Task JoinWaitlistReplaysAfterPromotionAndScopesTheKeyByUserAndOperation()
    {
        await using var factory = new ApiApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var users = await db.Set<User>().OrderBy(user => user.Id).Select(user => user.Id).Take(2).ToArrayAsync();
        var createdAt = new DateTime(2026, 11, 15, 8, 0, 0, DateTimeKind.Utc);
        var classId = await AddBookingClassAsync(db, createdAt, capacity: 3);
        var key = Guid.NewGuid().ToString("N");
        var promoted = new WaitlistEntry
        {
            ClassId = classId, UserId = users[0], Seats = 2, CreatedAt = createdAt, Status = WaitlistStatus.Promoted
        };
        db.Add(promoted);
        await db.SaveChangesAsync();
        var storedBody =
            $$"""{"id":{{promoted.Id}}, "classId":{{classId}}, "seats":2, "position":7, "createdAt":"2026-11-15T08:00:00Z"}""";

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            unitOfWork.Idempotency.AddSuccess(
                users[0], IdempotencyOperation.CreateEnrollment, key, classId, 1, "{}");
            unitOfWork.Idempotency.AddSuccess(
                users[0], IdempotencyOperation.JoinWaitlist, key, classId, 2, storedBody);
            unitOfWork.Idempotency.AddSuccess(
                users[1], IdempotencyOperation.JoinWaitlist, key, classId, 3, "{}");
            await unitOfWork.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        using var client = CreateAuthenticatedClient(factory, users[0]);
        using var replay = CreateRequest($$"""{"classId":{{classId}},"seats":2}""", key, WaitlistPath);
        using var replayResponse = await client.SendAsync(replay);
        Assert.AreEqual(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.AreEqual("application/json", replayResponse.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(storedBody, await replayResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(WaitlistStatus.Promoted, await db.Set<WaitlistEntry>().Where(row => row.Id == promoted.Id)
            .Select(row => row.Status).SingleAsync());

        using var otherClient = CreateAuthenticatedClient(factory, users[1]);
        using var otherReplay = CreateRequest($$"""{"classId":{{classId}},"seats":3}""", key, WaitlistPath);
        using var otherReplayResponse = await otherClient.SendAsync(otherReplay);
        Assert.AreEqual(HttpStatusCode.Created, otherReplayResponse.StatusCode);
        Assert.AreEqual("{}", await otherReplayResponse.Content.ReadAsStringAsync());

        foreach (var body in new[]
        {
            $$"""{"classId":{{classId}},"seats":3}""",
            """{"classId":9223372036854775807,"seats":2}"""
        })
        {
            using var conflict = CreateRequest(body, key, WaitlistPath);
            using var conflictResponse = await client.SendAsync(conflict);
            Assert.AreEqual(HttpStatusCode.Conflict, conflictResponse.StatusCode);
            Assert.AreEqual("application/problem+json", conflictResponse.Content.Headers.ContentType?.MediaType);
            var problem = await conflictResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual("idempotency-key-conflict", problem.GetProperty("reason").GetString());
        }

        Assert.AreEqual(3, await db.Set<IdempotencyEntry>().CountAsync(entry => entry.OriginalKey == key));
    }
}
