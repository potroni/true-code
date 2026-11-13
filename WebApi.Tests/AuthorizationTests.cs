using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DataAccessLayer.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
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

        SetBearerToken(client, token.AccessToken);

        using var identity = await client.GetAsync(IdentityPath);
        Assert.AreEqual(HttpStatusCode.OK, identity.StatusCode);
        var userId = await identity.Content.ReadFromJsonAsync<long>();
        Assert.IsTrue(userId > 0);
        using var withQuery = await client.GetAsync($"{IdentityPath}?userId={userId + 1}");
        Assert.AreEqual(HttpStatusCode.OK, withQuery.StatusCode);
        Assert.AreEqual(userId, await withQuery.Content.ReadFromJsonAsync<long>());

        await AssertStatusAsync(client, AdminPath, adminStatus);
    }

    private static void SetBearerToken(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
