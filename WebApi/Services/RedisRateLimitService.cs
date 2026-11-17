using System.Globalization;
using StackExchange.Redis;
using WebApi.Exceptions;

namespace WebApi.Services;

internal interface IBusinessRateLimiter
{
    Task<TimeSpan> CheckAsync(long userId, CancellationToken ct);
}

internal sealed class RedisRateLimitService(
    IConnectionMultiplexer connection,
    IConfiguration configuration,
    ILogger<RedisRateLimitService> logger) : IBusinessRateLimiter
{
    private readonly string _keyPrefix = configuration["RateLimiting:KeyPrefix"] ?? "truecode:rate-limit:";

    private const string ReadClock = """
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000000 + tonumber(time[2])

        """;

    // решение: одна операция на пользователя атомарно удаляет истекшие записи и проверяет квоту по времени Redis.
    internal const string CheckWindow = """
        local key = KEYS[1]
        local window = 60000000
        redis.call('ZREMRANGEBYSCORE', key, '-inf', string.format('%.0f', now - window))

        if redis.call('ZCARD', key) >= 60 then
            local oldest = redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')
            return tonumber(oldest[2]) + window - now
        end

        redis.call('ZADD', key, string.format('%.0f', now), ARGV[1])
        redis.call('PEXPIRE', key, 60001)
        return 0
        """;

    public async Task<TimeSpan> CheckAsync(long userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = _keyPrefix + userId.ToString(CultureInfo.InvariantCulture);
        try
        {
            var result = await connection.GetDatabase()
                .ScriptEvaluateAsync(ReadClock + CheckWindow, [key], [Guid.NewGuid().ToString("N")])
                .WaitAsync(ct);

            return TimeSpan.FromMicroseconds((long)result);
        }
        catch (Exception exception) when (exception is RedisConnectionException or RedisTimeoutException)
        {
            logger.LogWarning("Redis is unavailable for rate limiting ({ErrorType}).", exception.GetType().Name);
            throw new BusinessRateLimiterUnavailableException(exception);
        }
    }
}
