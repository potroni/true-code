using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WebApi.Auth;

namespace WebApi.Middleware;

internal sealed class BusinessRateLimitPolicy : IRateLimiterPolicy<string>
{
    public const string Name = "Business";

    public Func<OnRejectedContext, CancellationToken, ValueTask> OnRejected => WriteRejectionAsync;

    public RateLimitPartition<string> GetPartition(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true ||
            !ApiAuthorization.TryGetUserId(context.User, out var userId))
        {
            return RateLimitPartition.GetNoLimiter("anonymous");
        }

        // решение: фиксированное окно ASP.NET Core даёт общий атомарный счётчик на пользователя в экземпляре API.
        return RateLimitPartition.GetFixedWindowLimiter(userId.ToString(CultureInfo.InvariantCulture),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    }

    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            httpContext.Response.Headers.RetryAfter =
                Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        await httpContext.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(
            new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = new ProblemDetails { Status = StatusCodes.Status429TooManyRequests }
            });
    }
}
