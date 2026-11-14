using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace WebApi.Contracts;

public sealed partial class ContractDateModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext context)
    {
        var value = context.ValueProvider.GetValue(context.ModelName);
        if (value == ValueProviderResult.None)
        {
            return Task.CompletedTask;
        }

        context.ModelState.SetModelValue(context.ModelName, value);
        var input = value.FirstValue;

        if (context.ModelType == typeof(DateOnly))
        {
            if (DateOnly.TryParseExact(input, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
            {
                context.Result = ModelBindingResult.Success(date);
                return Task.CompletedTask;
            }

            context.ModelState.TryAddModelError(context.ModelName, "Expected a date in yyyy-MM-dd format.");
            return Task.CompletedTask;
        }

        if (input is not null && IsoTimestamp().IsMatch(input)
            && DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            context.Result = ModelBindingResult.Success(timestamp.UtcDateTime);
            return Task.CompletedTask;
        }

        context.ModelState.TryAddModelError(context.ModelName, "Expected an ISO 8601 date and time.");
        return Task.CompletedTask;
    }

    // решение: используем расширенный формат ISO 8601, совпадающий по разделителям с JSON-ответами.
    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(?::[0-9]{2}(?:\.[0-9]+)?)?(?:Z|[+-][0-9]{2}:[0-9]{2})?\z")]
    private static partial Regex IsoTimestamp();
}
