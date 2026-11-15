using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
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

    private static HttpClient CreateAuthenticatedClient(ApiApplicationFactory factory)
    {
        var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<AccessTokenService>()
            .Create(new User { Id = UserId, Email = "client@club.test", Role = "user" });
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

        public Task<EnrollmentResponse?> CreateAsync(
            long userId, long classId, int seats, string idempotencyKey, CancellationToken ct)
        {
            Arguments = (userId, classId, seats, idempotencyKey);
            return Task.FromResult<EnrollmentResponse?>(new(123, classId, seats, "active", CreatedAt));
        }
    }
}
