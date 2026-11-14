using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc.Infrastructure;
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

        // решение: фиксированное окно ASP.NET Core дает общий атомарный счетчик на пользователя в экземпляре API.
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

        // решение: ответ 429 всегда содержит ProblemDetails в JSON независимо от Accept.
        var problem = httpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>()
            .CreateProblemDetails(httpContext, StatusCodes.Status429TooManyRequests);
        await httpContext.Response.WriteAsJsonAsync(problem, options: null,
            contentType: "application/problem+json", cancellationToken: cancellationToken);
    }
}
