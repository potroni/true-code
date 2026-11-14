using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace WebApi.Contracts;

public sealed class LoadQuery : IValidatableObject
{
    [BindRequired]
    [ModelBinder(BinderType = typeof(ContractDateModelBinder))]
    public DateOnly From { get; init; }

    [BindRequired]
    [ModelBinder(BinderType = typeof(ContractDateModelBinder))]
    public DateOnly To { get; init; }

    public long? RoomId { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (To < From)
        {
            yield return new ValidationResult("To must be on or after from.", [nameof(To)]);
        }
    }
}
