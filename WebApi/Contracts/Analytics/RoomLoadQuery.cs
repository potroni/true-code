using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using WebApi.ModelBinding;

namespace WebApi.Contracts.Analytics;

public sealed class RoomLoadQuery : IValidatableObject
{
    [BindRequired]
    [ModelBinder(Name = "from", BinderType = typeof(ContractDateModelBinder))]
    public DateOnly From { get; init; }

    [BindRequired]
    [ModelBinder(Name = "to", BinderType = typeof(ContractDateModelBinder))]
    public DateOnly To { get; init; }

    [ModelBinder(Name = "roomId", BinderType = typeof(OptionalLongQueryModelBinder))]
    public long? RoomId { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (To < From)
        {
            yield return new ValidationResult("To must be on or after from.", [nameof(To)]);
        }
    }
}
