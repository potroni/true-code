using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace WebApi.Contracts;

public sealed class ScheduleQuery : IValidatableObject
{
    [BindRequired]
    [ModelBinder(BinderType = typeof(ContractDateModelBinder))]
    public DateTime From { get; init; }

    [BindRequired]
    [ModelBinder(BinderType = typeof(ContractDateModelBinder))]
    public DateTime To { get; init; }

    public long? RoomId { get; init; }

    [Range(1, 100)]
    public int Limit { get; init; } = 20;

    public string? ContinuationToken { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (To <= From)
        {
            yield return new ValidationResult("To must be later than from.", [nameof(To)]);
        }
    }
}
