namespace WebApi.Middleware;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class BusinessRateLimitAttribute : Attribute;
