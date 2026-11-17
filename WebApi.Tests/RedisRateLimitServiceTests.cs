using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using WebApi.Auth;
using WebApi.Services;

namespace WebApi.Tests;

[TestClass]
public class RedisRateLimitServiceTests
{
    private const string Schedule = "/schedule?from=2076-01-01T10:00:00Z&to=2076-01-01T11:00:00Z";

    [TestMethod]
    public async Task EightRoutesShareOneQuotaAcrossTwoHosts()
    {
        var prefix = $"tests:rate-limit:{Guid.NewGuid():N}:";
        using var first = new ApiApplicationFactory(prefix);
        using var second = new ApiApplicationFactory(prefix);
        using var firstClient = first.CreateClient();
        using var secondClient = second.CreateClient();
        Assert.AreNotSame(first.Services.GetRequiredService<IConnectionMultiplexer>(),
            second.Services.GetRequiredService<IConnectionMultiplexer>());

        var token = CreateToken(first, 1);
        Authenticate(firstClient, token);
        Authenticate(secondClient, token);

        var routes = new (HttpMethod Method, string Path, object? Body, HttpStatusCode Expected)[]
        {
            (HttpMethod.Get, Schedule, null, HttpStatusCode.OK),
            (HttpMethod.Get, "/analytics/load?from=2076-01-01&to=2076-01-01", null, HttpStatusCode.OK),
            (HttpMethod.Post, "/enrollments", new { classId = long.MaxValue, seats = 1 }, HttpStatusCode.NotFound),
            (HttpMethod.Get, $"/enrollments/{long.MaxValue}", null, HttpStatusCode.NotFound),
            (HttpMethod.Post, $"/enrollments/{long.MaxValue}/seats", new { seats = 1 }, HttpStatusCode.NotFound),
            (HttpMethod.Post, $"/enrollments/{long.MaxValue}/cancel", null, HttpStatusCode.NotFound),
            (HttpMethod.Post, "/enrollments/waitlist", new { classId = long.MaxValue, seats = 1 }, HttpStatusCode.NotFound),
            (HttpMethod.Post, $"/enrollments/waitlist/{long.MaxValue}/cancel", null, HttpStatusCode.NotFound)
        };

        for (var index = 0; index < 60; index++)
        {
            var route = routes[index % routes.Length];
            var client = (index / routes.Length + index) % 2 == 0 ? firstClient : secondClient;
            using var request = new HttpRequestMessage(route.Method, route.Path);
            if (route.Body is not null)
                request.Content = JsonContent.Create(route.Body);
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var response = await client.SendAsync(request);
            Assert.AreEqual(route.Expected, response.StatusCode, $"{route.Method} {route.Path}");
        }

        using var rejected = await secondClient.GetAsync(Schedule);
        await AssertProblemAsync(rejected, HttpStatusCode.TooManyRequests);
        Assert.IsTrue(rejected.Headers.RetryAfter?.Delta is { } retryAfter &&
                      retryAfter >= TimeSpan.FromSeconds(1) && retryAfter <= TimeSpan.FromSeconds(60));

        Authenticate(secondClient, CreateToken(first, 2));
        using var otherUser = await secondClient.GetAsync(Schedule);
        Assert.AreEqual(HttpStatusCode.OK, otherUser.StatusCode);
    }

    [TestMethod]
    public async Task ConcurrentHostsCanSpendLastPermitOnlyOnce()
    {
        var prefix = $"tests:rate-limit:{Guid.NewGuid():N}:";
        using var first = new ApiApplicationFactory(prefix);
        using var second = new ApiApplicationFactory(prefix);
        using var firstClient = first.CreateClient();
        using var secondClient = second.CreateClient();
        var token = CreateToken(first, 1);
        Authenticate(firstClient, token);
        Authenticate(secondClient, token);

        for (var index = 0; index < 59; index++)
        {
            using var response = await firstClient.GetAsync("/schedule");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var responses = await Task.WhenAll(firstClient.GetAsync("/schedule"), secondClient.GetAsync("/schedule"));
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];
        CollectionAssert.AreEquivalent(
            new[] { HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests },
            new[] { firstResponse.StatusCode, secondResponse.StatusCode });
    }

    [TestMethod]
    public async Task WindowExpiresAtExactlySixtySecondsOnRedis()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(TestDatabase.RedisConnectionString);
        var database = connection.GetDatabase();
        var key = $"tests:rate-limit:{Guid.NewGuid():N}:boundary";
        const long start = 1_700_000_000_000_000;
        var script = "local now = tonumber(ARGV[2])\n" + RedisRateLimitService.CheckWindow;

        async Task<long> EvaluateAsync(long now) => (long)await database.ScriptEvaluateAsync(
            script, [key], [Guid.NewGuid().ToString("N"), now]);

        try
        {
            Assert.AreEqual(0L, await EvaluateAsync(start));
            for (var index = 0; index < 59; index++)
                Assert.AreEqual(0L, await EvaluateAsync(start + 59_000_000));

            Assert.AreEqual(1L, await EvaluateAsync(start + 59_999_999));
            Assert.AreEqual(0L, await EvaluateAsync(start + 60_000_000));
            Assert.AreEqual(59_000_000L, await EvaluateAsync(start + 60_000_000));
            Assert.AreEqual(60L, await database.SortedSetLengthAsync(key));
            Assert.IsTrue(await database.KeyTimeToLiveAsync(key) > TimeSpan.Zero);
        }
        finally
        {
            await database.KeyDeleteAsync(key);
        }
    }

    [TestMethod]
    public async Task UnavailableRedisAtStartupReturns503OnlyForAuthenticatedBusinessRequests()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var factory = new ApiApplicationFactory($"tests:rate-limit:{Guid.NewGuid():N}:",
            $"{endpoint},connectTimeout=500");
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/health");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        using var anonymous = await client.GetAsync(Schedule);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        Authenticate(client, CreateToken(factory, 1));
        using var unavailable = await client.GetAsync(Schedule);
        await AssertProblemAsync(unavailable, HttpStatusCode.ServiceUnavailable);
        Assert.IsNull(unavailable.Headers.RetryAfter);
    }

    private static string CreateToken(ApiApplicationFactory factory, long userId) =>
        factory.Services.GetRequiredService<AccessTokenService>().Create(new User
        {
            Id = userId, Email = "limiter@club.test", Role = ApiAuthorization.AdminRole
        }).AccessToken;

    private static void Authenticate(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode statusCode)
    {
        Assert.AreEqual(statusCode, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual((int)statusCode, problem.GetProperty("status").GetInt32());
    }
}
