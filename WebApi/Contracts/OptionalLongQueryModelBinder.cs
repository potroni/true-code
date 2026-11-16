using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace WebApi.Contracts;

public sealed class OptionalLongQueryModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext context)
    {
        var value = context.ValueProvider.GetValue(context.ModelName);
        if (value == ValueProviderResult.None)
        {
            return Task.CompletedTask;
        }

        context.ModelState.SetModelValue(context.ModelName, value);
        if (long.TryParse(value.FirstValue, NumberStyles.Integer, value.Culture, out var result))
        {
            context.Result = ModelBindingResult.Success(result);
        }
        else
        {
            context.ModelState.TryAddModelError(context.ModelName, "Expected a 64-bit integer.");
        }

        return Task.CompletedTask;
    }
}
