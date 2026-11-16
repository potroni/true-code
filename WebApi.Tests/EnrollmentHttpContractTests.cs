using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.Hosting;
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
    public async Task CreateReportsUnavailableImplementationAsProblemDetails()
    {
        using var factory = new ApiApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        using var request = CreateRequest(ValidBody, "key");
        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
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

    private sealed class EnrollmentServiceStub : IEnrollmentService
    {
        public static readonly DateTimeOffset CreatedAt = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);

        public (long UserId, long ClassId, int Seats, string Key)? Arguments { get; private set; }

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
