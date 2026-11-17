using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebApi.Tests;

public class ApiApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _rateLimitKeyPrefix;
    private readonly string? _redisConnectionString;

    public ApiApplicationFactory() : this($"tests:rate-limit:{Guid.NewGuid():N}:")
    {
    }

    internal ApiApplicationFactory(string rateLimitKeyPrefix, string? redisConnectionString = null)
    {
        _rateLimitKeyPrefix = rateLimitKeyPrefix;
        _redisConnectionString = redisConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = TestDatabase.PostgresConnectionString,
                ["ConnectionStrings:Redis"] = _redisConnectionString ?? TestDatabase.RedisConnectionString,
                ["RateLimiting:KeyPrefix"] = _rateLimitKeyPrefix
            });
        });
    }
}
