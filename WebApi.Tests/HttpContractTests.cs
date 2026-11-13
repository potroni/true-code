using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Auth;
using WebApi.Contracts;
using WebApi.Controllers;

namespace WebApi.Tests;

[TestClass]
public class HttpContractTests
{
    private const string Path = "/test/http-contract";

    [TestMethod]
    [DataRow("class-seats", null)]
    [DataRow("class-seats", "")]
    [DataRow("class-seats", "{")]
    [DataRow("class-seats", "null")]
    [DataRow("class-seats", """{"seats":1}""")]
    [DataRow("class-seats", """{"classId":0}""")]
    [DataRow("class-seats", """{"classId":null,"seats":1}""")]
    [DataRow("class-seats", """{"classId":"0","seats":1}""")]
    [DataRow("class-seats", """{"classId":0,"seats":0}""")]
    [DataRow("change-seats", "{}")]
    [DataRow("change-seats", null)]
    [DataRow("change-seats", """{"seats":"1"}""")]
    [DataRow("change-seats", """{"seats":0}""")]
    public async Task InvalidBodyReturnsProblemDetails(string endpoint, string? body)
    {
        using var factory = new ContractApplicationFactory();
        using var client = CreateClient(factory);
        using var content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"{Path}/{endpoint}", content);

        await ReadProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task NumericZeroIdIsBoundAndUnknownPropertiesAreIgnored()
    {
        using var factory = new ContractApplicationFactory();
        using var client = CreateClient(factory);
        using var content = new StringContent("""{"classId":0,"seats":1,"userId":999}""",
            Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"{Path}/class-seats", content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(0L, json.GetProperty("classId").GetInt64());
        Assert.AreEqual(1, json.GetProperty("seats").GetInt32());
        Assert.IsFalse(json.TryGetProperty("userId", out _));
    }

    [TestMethod]
    public async Task NonemptyBodyWithUnsupportedContentTypeReturns415()
    {
        using var factory = new ContractApplicationFactory();
        using var client = CreateClient(factory);
        using var content = new StringContent("""{"classId":1,"seats":1}""");
        using var response = await client.PostAsync($"{Path}/class-seats", content);

        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [TestMethod]
    [DataRow("2026-09-22T10:00:00")]
    [DataRow("2026-09-22T10:00:00Z")]
    [DataRow("2026-09-22T12:00:00%2B02:00")]
    [DataRow("2026-09-22T07:00:00-03:00")]
    public async Task ScheduleTimestampsAreReturnedInUtc(string from)
    {
        using var factory = new ContractApplicationFactory();
        using var client = CreateClient(factory);
        using var response = await client.GetAsync($"{Path}/schedule?from={from}&to=2026-09-22T13:00");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("2026-09-22T10:00:00Z", json.GetProperty("from").GetString());
        Assert.AreEqual(20, json.GetProperty("limit").GetInt32());
    }

    [TestMethod]
    [DataRow("schedule?to=2026-09-22T13:00")]
    [DataRow("schedule?from=2026-09-22T10:00")]
    [DataRow("schedule?from=2026-09-22&to=2026-09-22T13:00")]
    [DataRow("schedule?from=2026-09-22%2010:00&to=2026-09-22T13:00")]
    [DataRow("schedule?from=2026-09-22T10:00&to=2026-09-22T13:00&roomId=no")]
    [DataRow("schedule?from=2026-09-22T10:00&to=2026-09-22T12:00%2B02:00")]
    [DataRow("schedule?from=2026-09-22T10:00&to=2026-09-22T13:00&limit=0")]
    [DataRow("load?from=2026-09-22T10:00&to=2026-09-22")]
    [DataRow("load?from=2026-9-22&to=2026-09-22")]
    [DataRow("load?from=2026-02-30&to=2026-09-22")]
    [DataRow("load?from=2026-09-22")]
    [DataRow("load?from=2026-09-23&to=2026-09-22")]
    [DataRow("ids/no")]
    public async Task InvalidQueryOrRouteValueReturnsProblemDetails(string endpoint)
    {
        using var factory = new ContractApplicationFactory();
        using var client = CreateClient(factory);
        using var response = await client.GetAsync($"{Path}/{endpoint}");

        await ReadProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task AnalyticsDatesUseDateOnlyFormat()
    {
        using var factory = new ContractApplicationFactory();
        using var client = CreateClient(factory);
        using var response = await client.GetAsync($"{Path}/load?from=2026-09-22&to=2026-09-22");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("2026-09-22", json.GetProperty("from").GetString());
        Assert.AreEqual("2026-09-22", json.GetProperty("to").GetString());
    }

    [TestMethod]
    public async Task DomainConflictHasTopLevelReason()
    {
        using var factory = new ContractApplicationFactory();
        using var client = CreateClient(factory);
        using var response = await client.GetAsync($"{Path}/conflict");

        var problem = await ReadProblemAsync(response, HttpStatusCode.Conflict);
        Assert.AreEqual("capacity-exceeded", problem.GetProperty("reason").GetString());
    }

    private static HttpClient CreateClient(ContractApplicationFactory factory)
    {
        var client = factory.CreateClient();
        var tokenService = factory.Services.GetRequiredService<AccessTokenService>();
        var token = tokenService.Create(new User { Id = 1, Email = "test@club.test", Role = "admin" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.AreEqual(status, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual((int)status, problem.GetProperty("status").GetInt32());
        Assert.IsFalse(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        return problem;
    }

    private sealed class ContractApplicationFactory : ApiApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
                services.AddControllers().AddApplicationPart(typeof(HttpContractProbeController).Assembly));
        }
    }
}

[ApiController]
[Route("test/http-contract")]
public class HttpContractProbeController : BaseApiController
{
    [HttpPost("class-seats")]
    public IActionResult ClassSeats([FromRequiredBody] ClassSeatsRequest request) => Ok(request);

    [HttpPost("change-seats")]
    public IActionResult ChangeSeats([FromRequiredBody] ChangeSeatsRequest request) => Ok(request);

    [HttpGet("schedule")]
    public IActionResult Schedule([FromQuery] ScheduleQuery query) => Ok(query);

    [HttpGet("load")]
    public IActionResult Load([FromQuery] LoadQuery query) => Ok(query);

    [HttpGet("ids/{id}")]
    public IActionResult Id([FromRoute] long id) => Ok(id);

    [HttpGet("conflict")]
    public IActionResult DomainConflict() => ConflictWithReason("capacity-exceeded");
}
