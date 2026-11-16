using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Auth;
using WebApi.Contracts;
using WebApi.Services;

namespace WebApi.Tests;

[TestClass]
public class ScheduleHttpContractTests
{
    private const long UserId = 42;
    private const string Path = "/schedule";
    private const string ValidUrl = Path + "?from=2026-09-22T12:00%2B02:00&to=2026-09-22T13:00";

    [TestMethod]
    [DataRow(null)]
    [DataRow("invalid")]
    public async Task RequiresValidJwt(string? token)
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync(ValidUrl);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [DataRow("?to=2026-09-22T13:00")]
    [DataRow("?from=2026-09-22T10:00")]
    [DataRow("?from=2026-09-22&to=2026-09-22T13:00")]
    [DataRow("?from=2026-09-22T10:00&to=2026-09-22T25:00")]
    [DataRow("?from=2026-09-22T10:00&to=2026-09-22T12:00%2B02:00")]
    [DataRow("?from=2026-09-22T10:00&to=2026-09-22T09:59")]
    [DataRow("?from=2026-09-22T10:00&to=2026-09-22T13:00&roomId=no")]
    [DataRow("?from=2026-09-22T10:00&to=2026-09-22T13:00&limit=no")]
    [DataRow("?from=2026-09-22T10:00&to=2026-09-22T13:00&limit=0")]
    [DataRow("?from=2026-09-22T10:00&to=2026-09-22T13:00&limit=101")]
    [DataRow("?from=2026-09-22T10:00&to=2026-09-22T13:00&continuationToken=opaque")]
    [DataRow("?from=2026-09-22T10:00&to=2026-09-22T13:00&continuationToken=")]
    [DataRow("?from=2026-09-22T10:00&to=2026-09-22T13:00&continuationToken=%20")]
    public async Task InvalidQueryReturnsValidationProblem(string queryString)
    {
        using var factory = new ApiApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        using var response = await client.GetAsync(Path + queryString);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    [DataRow("", 20, "next")]
    [DataRow("&limit=1", 1, "next")]
    [DataRow("&limit=100", 100, null)]
    public async Task ForwardsValidatedQueryAndJwtUserAndReturnsClosedResponse(
        string limit, int expectedLimit, string? nextToken)
    {
        var service = new ScheduleServiceStub(nextToken);
        using var factory = new ScheduleApplicationFactory(service);
        using var client = CreateAuthenticatedClient(factory);
        var token = new ScheduleCursor(new DateTime(2026, 9, 22, 11, 0, 0, DateTimeKind.Utc), 1).Encode();
        using var response = await client.GetAsync(
            ValidUrl + $"&roomId=4294967296&continuationToken={token}&userId=99" + limit);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(UserId, service.UserId);
        var query = service.Query;
        Assert.IsNotNull(query);
        Assert.AreEqual(new DateTime(2026, 9, 22, 10, 0, 0, DateTimeKind.Utc), query.From);
        Assert.AreEqual(new DateTime(2026, 9, 22, 13, 0, 0, DateTimeKind.Utc), query.To);
        Assert.AreEqual(DateTimeKind.Utc, query.From.Kind);
        Assert.AreEqual(DateTimeKind.Utc, query.To.Kind);
        Assert.AreEqual(4294967296L, query.RoomId);
        Assert.AreEqual(expectedLimit, query.Limit);
        Assert.AreEqual(token, query.ContinuationToken);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertProperties(json, "items", "continuationToken");
        var continuationToken = json.GetProperty("continuationToken");
        if (nextToken is null)
            Assert.AreEqual(JsonValueKind.Null, continuationToken.ValueKind);
        else
            Assert.AreEqual(nextToken, continuationToken.GetString());

        var item = json.GetProperty("items").EnumerateArray().Single();
        AssertProperties(item, "classId", "title", "startsAt", "durationMinutes", "room", "capacity",
            "freeSeats", "mySeats");
        Assert.AreEqual(1L, item.GetProperty("classId").GetInt64());
        Assert.AreEqual("Yoga", item.GetProperty("title").GetString());
        Assert.AreEqual("2026-09-22T10:00:00Z", item.GetProperty("startsAt").GetString());
        Assert.AreEqual(60, item.GetProperty("durationMinutes").GetInt32());
        Assert.AreEqual(10, item.GetProperty("capacity").GetInt32());
        Assert.AreEqual(7, item.GetProperty("freeSeats").GetInt32());
        Assert.AreEqual(2, item.GetProperty("mySeats").GetInt32());
        var room = item.GetProperty("room");
        AssertProperties(room, "id", "name");
        Assert.AreEqual(2L, room.GetProperty("id").GetInt64());
        Assert.AreEqual("Studio", room.GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task EmptyPageContainsExplicitNullToken()
    {
        using var factory = new ApiApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        using var response = await client.GetAsync(ValidUrl + $"&roomId={long.MaxValue}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertProperties(json, "items", "continuationToken");
        Assert.AreEqual(0, json.GetProperty("items").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, json.GetProperty("continuationToken").ValueKind);
    }

    [TestMethod]
    public async Task SharesRateLimitWithEnrollmentRoutes()
    {
        using var factory = new ApiApplicationFactory();
        using var client = CreateAuthenticatedClient(factory);
        for (var i = 0; i < 60; i++)
        {
            using var response = await client.GetAsync("/enrollments/not-a-number");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var rejected = await client.GetAsync(ValidUrl);

        Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [TestMethod]
    public async Task DocumentsQueryParametersAndResponsesInOpenApi()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var operation = document.GetProperty("paths").GetProperty(Path).GetProperty("get");
        var parameters = operation.GetProperty("parameters").EnumerateArray()
            .ToDictionary(parameter => parameter.GetProperty("name").GetString()!);
        CollectionAssert.AreEquivalent(new[] { "from", "to", "roomId", "limit", "continuationToken" },
            parameters.Keys.ToArray());
        Assert.IsTrue(parameters["from"].GetProperty("required").GetBoolean());
        Assert.IsTrue(parameters["to"].GetProperty("required").GetBoolean());
        Assert.AreEqual("date-time", parameters["from"].GetProperty("schema").GetProperty("format").GetString());
        Assert.AreEqual("int64", parameters["roomId"].GetProperty("schema").GetProperty("format").GetString());
        var limit = parameters["limit"].GetProperty("schema");
        Assert.AreEqual(20, limit.GetProperty("default").GetInt32());
        Assert.AreEqual(1, limit.GetProperty("minimum").GetInt32());
        Assert.AreEqual(100, limit.GetProperty("maximum").GetInt32());

        var responses = operation.GetProperty("responses");
        AssertProperties(responses, "200", "400", "401", "429");
        var schema = responses.GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        Assert.AreEqual("#/components/schemas/ScheduleResponse", schema.GetProperty("$ref").GetString());
        var page = document.GetProperty("components").GetProperty("schemas").GetProperty("ScheduleResponse");
        CollectionAssert.AreEquivalent(new[] { "items", "continuationToken" },
            page.GetProperty("required").EnumerateArray().Select(field => field.GetString()).ToArray());
        var token = page.GetProperty("properties").GetProperty("continuationToken");
        Assert.AreEqual("string", token.GetProperty("type").GetString());
        Assert.IsTrue(token.GetProperty("nullable").GetBoolean());
    }

    private static void AssertProperties(JsonElement json, params string[] expected) =>
        CollectionAssert.AreEquivalent(expected, json.EnumerateObject().Select(property => property.Name).ToArray());

    private static HttpClient CreateAuthenticatedClient(ApiApplicationFactory factory)
    {
        var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<AccessTokenService>()
            .Create(new User { Id = UserId, Email = "client@club.test", Role = "user" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private sealed class ScheduleApplicationFactory(IScheduleService service) : ApiApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton(service));
        }
    }

    private sealed class ScheduleServiceStub(string? nextToken) : IScheduleService
    {
        public ScheduleQuery? Query { get; private set; }
        public long UserId { get; private set; }

        public Task<ScheduleResponse> GetPageAsync(ScheduleQuery query, long userId, CancellationToken ct)
        {
            Query = query;
            UserId = userId;
            return Task.FromResult(new ScheduleResponse(
                [new ScheduleItemResponse(1, "Yoga", query.From, 60, new ScheduleRoomResponse(2, "Studio"), 10, 7, 2)],
                nextToken));
        }
    }
}
