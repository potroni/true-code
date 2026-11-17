using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Auth;

namespace WebApi.Tests;

[TestClass]
public class AnalyticsHttpContractTests
{
    private const string Path = "/analytics/load";
    private const string ValidUrl = Path + "?from=2026-09-22&to=2026-09-22";

    [TestMethod]
    public async Task RequiresValidJwtAndAdminRole()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();

        using var anonymous = await client.GetAsync(ValidUrl);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
        using var invalid = await client.GetAsync(ValidUrl);
        Assert.AreEqual(HttpStatusCode.Unauthorized, invalid.StatusCode);

        Authenticate(factory, client, "user");
        using var forbidden = await client.GetAsync(ValidUrl);
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [TestMethod]
    [DataRow("?to=2026-09-22")]
    [DataRow("?from=2026-09-22")]
    [DataRow("?query.from=2026-09-22&query.to=2026-09-22")]
    [DataRow("?from=2026-09-22&query.to=2026-09-22")]
    [DataRow("?from=2026-9-22&to=2026-09-22")]
    [DataRow("?from=2026-09-22&to=2026-09-22T00:00:00Z")]
    [DataRow("?from=2026-09-23&to=2026-09-22")]
    [DataRow("?from=2026-09-22&to=2026-09-22&roomId=no")]
    [DataRow("?from=2026-09-22&to=2026-09-22&roomId=")]
    [DataRow("?from=2026-09-22&to=2026-09-22&roomId=9223372036854775808")]
    public async Task RejectsInvalidParameters(string queryString)
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        Authenticate(factory, client, ApiAuthorization.AdminRole);

        using var response = await client.GetAsync(Path + queryString);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task ReturnsExactJsonForSingleDayAndEmptyItemsWhenNothingMatches()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        Authenticate(factory, client, ApiAuthorization.AdminRole);

        const string date = "2076-07-08";
        var startsAt = new DateTime(2076, 7, 8, 0, 0, 0, DateTimeKind.Utc);
        var room = new Room { Name = "Load HTTP contract room" };
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
            var userId = await db.Users.OrderBy(user => user.Id).Select(user => user.Id).FirstAsync();
            var scheduledClass = new Class
            {
                Room = room, Title = "Yoga", StartsAt = startsAt, DurationMinutes = 60, Capacity = 12
            };
            db.Enrollments.Add(new Enrollment
            {
                Class = scheduledClass, UserId = userId, Status = "active", Seats = 3, CreatedAt = startsAt.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }

        var url = $"{Path}?from={date}&to={date}";
        var item = (await GetItemsAsync(client, $"{url}&query.from=invalid&query.to=invalid&query.roomId=invalid"))
            .EnumerateArray().Single();
        AssertProperties(item, "date", "room", "classesCount", "totalCapacity", "bookedSeats");
        Assert.AreEqual(date, item.GetProperty("date").GetString());
        var roomJson = item.GetProperty("room");
        AssertProperties(roomJson, "id", "name");
        Assert.AreEqual(room.Id, roomJson.GetProperty("id").GetInt64());
        Assert.AreEqual(room.Name, roomJson.GetProperty("name").GetString());
        Assert.AreEqual(1, item.GetProperty("classesCount").GetInt32());
        Assert.AreEqual(12, item.GetProperty("totalCapacity").GetInt32());
        Assert.AreEqual(3, item.GetProperty("bookedSeats").GetInt32());

        var unknownRoom = await GetItemsAsync(client, $"{url}&roomId={long.MaxValue}");
        Assert.AreEqual(0, unknownRoom.GetArrayLength());
        var emptyPeriod = await GetItemsAsync(client, Path + "?from=2076-07-09&to=2076-07-09");
        Assert.AreEqual(0, emptyPeriod.GetArrayLength());
    }

    [TestMethod]
    public async Task SharesRateLimitWithSchedule()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        Authenticate(factory, client, ApiAuthorization.AdminRole);

        for (var i = 0; i < 60; i++)
        {
            using var response = await client.GetAsync("/schedule");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var rejected = await client.GetAsync(ValidUrl);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.AreEqual("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task DocumentsParametersAndResponseInOpenApi()
    {
        using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var operation = document.GetProperty("paths").GetProperty(Path).GetProperty("get");
        var parameters = operation.GetProperty("parameters").EnumerateArray()
            .ToDictionary(parameter => parameter.GetProperty("name").GetString()!);
        CollectionAssert.AreEquivalent(new[] { "from", "to", "roomId" }, parameters.Keys.ToArray());
        foreach (var name in new[] { "from", "to" })
        {
            Assert.IsTrue(parameters[name].GetProperty("required").GetBoolean());
            Assert.AreEqual("date", parameters[name].GetProperty("schema").GetProperty("format").GetString());
        }

        Assert.AreEqual("int64", parameters["roomId"].GetProperty("schema").GetProperty("format").GetString());
        var responses = operation.GetProperty("responses");
        AssertProperties(responses, "200", "400", "401", "403", "429");
        var responseSchema = responses.GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        Assert.AreEqual("#/components/schemas/LoadResponse", responseSchema.GetProperty("$ref").GetString());

        var schemas = document.GetProperty("components").GetProperty("schemas");
        var response = schemas.GetProperty("LoadResponse").GetProperty("properties");
        AssertProperties(response, "items");
        Assert.AreEqual("array", response.GetProperty("items").GetProperty("type").GetString());
        var item = schemas.GetProperty("LoadItemResponse").GetProperty("properties");
        AssertProperties(item, "date", "room", "classesCount", "totalCapacity", "bookedSeats");
        Assert.AreEqual("date", item.GetProperty("date").GetProperty("format").GetString());
        AssertProperties(schemas.GetProperty("LoadRoomResponse").GetProperty("properties"), "id", "name");
    }

    private static async Task<JsonElement> GetItemsAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertProperties(body, "items");
        return body.GetProperty("items");
    }

    private static void AssertProperties(JsonElement json, params string[] expected) =>
        CollectionAssert.AreEquivalent(expected, json.EnumerateObject().Select(property => property.Name).ToArray());

    private static void Authenticate(ApiApplicationFactory factory, HttpClient client, string role)
    {
        var token = factory.Services.GetRequiredService<AccessTokenService>()
            .Create(new User { Id = 1, Email = "test@club.test", Role = role });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
    }
}
