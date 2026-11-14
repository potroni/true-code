using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Auth;
using WebApi.Controllers;

namespace WebApi.Tests;

[TestClass]
public class AuthorizationTests
{
    private const string IdentityPath = "/test/authorization";
    private const string AdminPath = IdentityPath + "/admin";

    [TestMethod]
    [DataRow(IdentityPath)]
    [DataRow(AdminPath)]
    public async Task ProtectedEndpointRejectsMissingAndInvalidTokens(string path)
    {
        using var factory = new AuthorizationApplicationFactory();
        using var client = factory.CreateClient();

        await AssertStatusAsync(client, path, HttpStatusCode.Unauthorized);

        SetBearerToken(client, "invalid-token");
        await AssertStatusAsync(client, path, HttpStatusCode.Unauthorized);

        var tokenService = factory.Services.GetRequiredService<AccessTokenService>();
        var token = tokenService.Create(new User { Id = 0, Email = "test@club.test", Role = "admin" });
        SetBearerToken(client, token.AccessToken);
        await AssertStatusAsync(client, path, HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    [DataRow("user001@club.test", HttpStatusCode.Forbidden)]
    [DataRow("admin001@club.test", HttpStatusCode.OK)]
    public async Task AnonymousLoginIssuesTokenWithUserIdentityAndRole(string email, HttpStatusCode adminStatus)
    {
        using var factory = new AuthorizationApplicationFactory();
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/login",
            new LoginRequest { Email = email, Password = "Password123!" });
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        var token = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.IsNotNull(token);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var expectedUserId = await dbContext.Set<User>()
            .Where(user => user.Email == email)
            .Select(user => user.Id)
            .SingleAsync();

        SetBearerToken(client, token.AccessToken);

        using var identity = await client.GetAsync(IdentityPath);
        Assert.AreEqual(HttpStatusCode.OK, identity.StatusCode);
        var userId = await identity.Content.ReadFromJsonAsync<long>();
        Assert.AreEqual(expectedUserId, userId);
        using var withQuery = await client.GetAsync($"{IdentityPath}?userId={userId + 1}");
        Assert.AreEqual(HttpStatusCode.OK, withQuery.StatusCode);
        Assert.AreEqual(userId, await withQuery.Content.ReadFromJsonAsync<long>());

        await AssertStatusAsync(client, AdminPath, adminStatus);
    }

    [TestMethod]
    public async Task AnonymousLoginRejectsIncorrectPassword()
    {
        using var factory = new AuthorizationApplicationFactory();
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/login",
            new LoginRequest { Email = "user001@club.test", Password = "incorrect-password" });

        Assert.AreEqual(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    private static void SetBearerToken(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    [TestMethod]
    public async Task BusinessEndpointsShareSixtyRequestsPerUser()
    {
        using var factory = new AuthorizationApplicationFactory();
        using var client = factory.CreateClient();
        var tokenService = factory.Services.GetRequiredService<AccessTokenService>();
        SetBearerToken(client, tokenService.Create(new User
        {
            Id = 1, Email = "first@club.test", Role = ApiAuthorization.AdminRole
        }).AccessToken);

        await Task.WhenAll(Enumerable.Range(0, 60).Select(index =>
            AssertStatusAsync(client, index % 2 == 0 ? IdentityPath : AdminPath, HttpStatusCode.OK)));

        SetBearerToken(client, tokenService.Create(new User
        {
            Id = 1, Email = "updated@club.test", Role = ApiAuthorization.AdminRole
        }).AccessToken);
        using var rejected = await client.GetAsync(AdminPath);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.AreEqual("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(429, problem.GetProperty("status").GetInt32());

        using var htmlRequest = new HttpRequestMessage(HttpMethod.Get, IdentityPath);
        htmlRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        using var htmlRejected = await client.SendAsync(htmlRequest);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, htmlRejected.StatusCode);
        Assert.AreEqual("application/problem+json", htmlRejected.Content.Headers.ContentType?.MediaType);
        var htmlProblem = await htmlRejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(429, htmlProblem.GetProperty("status").GetInt32());

        using var login = await client.PostAsJsonAsync("/auth/login",
            new LoginRequest { Email = "user001@club.test", Password = "Password123!" });
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);

        client.DefaultRequestHeaders.Authorization = null;
        await AssertStatusAsync(client, IdentityPath, HttpStatusCode.Unauthorized);

        SetBearerToken(client, tokenService.Create(new User
        {
            Id = 2, Email = "second@club.test", Role = "user"
        }).AccessToken);
        await AssertStatusAsync(client, IdentityPath, HttpStatusCode.OK);
    }

    private static async Task AssertStatusAsync(HttpClient client, string path, HttpStatusCode expected)
    {
        using var response = await client.GetAsync(path);
        Assert.AreEqual(expected, response.StatusCode);
    }

    private sealed class AuthorizationApplicationFactory : ApiApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
                services.AddControllers().AddApplicationPart(typeof(AuthorizationProbeController).Assembly));
        }
    }
}

[ApiController]
[Route("test/authorization")]
public class AuthorizationProbeController : BaseApiController
{
    [HttpGet]
    public ActionResult<long> Identity() => CurrentUserId;

    [Authorize(Policy = ApiAuthorization.AdminPolicy)]
    [HttpGet("admin")]
    public IActionResult Admin() => Ok();
}
