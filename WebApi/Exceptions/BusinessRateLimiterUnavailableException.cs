namespace WebApi.Exceptions;

internal sealed class BusinessRateLimiterUnavailableException(Exception innerException)
    : Exception("The business rate limiter is unavailable.", innerException)
{
}
