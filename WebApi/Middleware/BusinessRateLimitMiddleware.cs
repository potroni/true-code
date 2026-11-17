using System.Globalization;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using StackExchange.Redis;
using WebApi.Auth;
using WebApi.Services;

namespace WebApi.Middleware;

internal sealed class BusinessRateLimitMiddleware(
    RequestDelegate next,
    RedisBusinessRateLimiter limiter,
    ILogger<BusinessRateLimitMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<BusinessRateLimitAttribute>() is not null &&
            context.User.Identity?.IsAuthenticated == true &&
            ApiAuthorization.TryGetUserId(context.User, out var userId))
        {
            TimeSpan retryAfter;
            try
            {
                retryAfter = await limiter.CheckAsync(userId, context.RequestAborted);
            }
            catch (Exception exception) when (exception is RedisConnectionException or RedisTimeoutException)
            {
                logger.LogWarning("Redis is unavailable for rate limiting ({ErrorType}).", exception.GetType().Name);
                await WriteRejectionAsync(context, StatusCodes.Status503ServiceUnavailable);
                return;
            }

            if (retryAfter > TimeSpan.Zero)
            {
                context.Response.Headers.RetryAfter =
                    Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                await WriteRejectionAsync(context, StatusCodes.Status429TooManyRequests);
                return;
            }
        }

        await next(context);
    }

    private static Task WriteRejectionAsync(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;

        // решение: отказ лимитера всегда содержит ProblemDetails в JSON независимо от Accept.
        var problem = context.RequestServices.GetRequiredService<ProblemDetailsFactory>()
            .CreateProblemDetails(context, statusCode);
        return context.Response.WriteAsJsonAsync(problem, options: null,
            contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}
