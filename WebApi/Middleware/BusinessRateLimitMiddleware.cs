using System.Globalization;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using WebApi.Auth;

namespace WebApi.Middleware;

internal sealed class BusinessRateLimitMiddleware(RequestDelegate next, TimeProvider timeProvider)
{
    private const int PermitLimit = 60;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private readonly object _lock = new();
    private readonly Dictionary<long, Queue<long>> _requestsByUser = new();
    private readonly Queue<(long UserId, long Timestamp)> _admittedRequests = new();

    public Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<BusinessRateLimitAttribute>() is not null &&
            context.User.Identity?.IsAuthenticated == true &&
            ApiAuthorization.TryGetUserId(context.User, out var userId) &&
            GetRetryAfter(userId) is { } retryAfter)
        {
            return WriteRejectionAsync(context, retryAfter);
        }

        return next(context);
    }

    private TimeSpan? GetRetryAfter(long userId)
    {
        // решение: монотонные метки допущенных запросов сохраняют точное окно, а общая очередь удаляет истекшие записи в порядке их добавления.
        lock (_lock)
        {
            var now = timeProvider.GetTimestamp();
            while (_admittedRequests.TryPeek(out var oldest) &&
                   timeProvider.GetElapsedTime(oldest.Timestamp, now) >= Window)
            {
                _admittedRequests.Dequeue();
                var userRequests = _requestsByUser[oldest.UserId];
                userRequests.Dequeue();
                if (userRequests.Count == 0)
                    _requestsByUser.Remove(oldest.UserId);
            }

            if (!_requestsByUser.TryGetValue(userId, out var requests))
            {
                requests = new Queue<long>();
                _requestsByUser.Add(userId, requests);
            }

            if (requests.Count >= PermitLimit)
                return Window - timeProvider.GetElapsedTime(requests.Peek(), now);

            requests.Enqueue(now);
            _admittedRequests.Enqueue((userId, now));
            return null;
        }
    }

    private static Task WriteRejectionAsync(HttpContext context, TimeSpan retryAfter)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter =
            Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        // решение: ответ 429 всегда содержит ProblemDetails в JSON независимо от Accept.
        var problem = context.RequestServices.GetRequiredService<ProblemDetailsFactory>()
            .CreateProblemDetails(context, StatusCodes.Status429TooManyRequests);
        return context.Response.WriteAsJsonAsync(problem, options: null,
            contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}
