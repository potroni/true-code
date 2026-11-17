namespace WebApi.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class BusinessRateLimitAttribute : Attribute;
