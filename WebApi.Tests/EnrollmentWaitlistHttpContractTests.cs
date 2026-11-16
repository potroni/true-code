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
    public async Task JoinWaitlistSharesTheBusinessRateLimit()
    {
        using var factory = new EnrollmentApplicationFactory(new EnrollmentServiceStub
        {
            WaitlistResult = new JoinWaitlistResult.ClassNotFound()
        });
        using var client = CreateAuthenticatedClient(factory);
        for (var index = 0; index < 30; index++)
        {
            using var details = await client.GetAsync("/enrollments/1");
            Assert.AreEqual(HttpStatusCode.NotFound, details.StatusCode);
            using var request = CreateRequest(ValidBody, "key", WaitlistPath);
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var rejectedRequest = CreateRequest(ValidBody, "key", WaitlistPath);
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
        AssertProperties(responses, "201", "400", "401", "404", "409", "429", "501");
        var schema = responses.GetProperty("201").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        Assert.AreEqual("#/components/schemas/JoinWaitlistResponse", schema.GetProperty("$ref").GetString());
        var properties = document.GetProperty("components").GetProperty("schemas")
            .GetProperty("JoinWaitlistResponse").GetProperty("properties");
        AssertProperties(properties, "id", "classId", "seats", "position", "createdAt");
        Assert.AreEqual("date-time", properties.GetProperty("createdAt").GetProperty("format").GetString());
    }

    [TestMethod]
    public async Task JoinWaitlistReportsPendingImplementationWithTheProductionService()
    {
        using var factory = new ApiApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        using var request = CreateRequest(ValidBody, "key", WaitlistPath);
        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task JoinWaitlistReplaysStoredPositionAndScopesTheKeyByUserAndOperation()
    {
        await using var factory = new ApiApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var db = unitOfWork.DbContext;
        var users = await db.Set<User>().OrderBy(user => user.Id).Select(user => user.Id).Take(2).ToArrayAsync();
        var classId = await db.Set<Class>().Select(activity => activity.Id).FirstAsync();
        var key = Guid.NewGuid().ToString("N");
        var storedBody =
            $$"""{"id":123, "classId":{{classId}}, "seats":2, "position":7, "createdAt":"2026-11-15T08:00:00Z"}""";

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
