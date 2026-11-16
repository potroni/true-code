using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace WebApi.Contracts;

public sealed class ScheduleQuery : IValidatableObject
{
    [BindRequired]
    [ModelBinder(Name = "from", BinderType = typeof(ContractDateModelBinder))]
    public DateTime From { get; init; }

    [BindRequired]
    [ModelBinder(Name = "to", BinderType = typeof(ContractDateModelBinder))]
    public DateTime To { get; init; }

    [ModelBinder(Name = "roomId", BinderType = typeof(OptionalLongQueryModelBinder))]
    public long? RoomId { get; init; }

    [FromQuery(Name = "limit")]
    [DefaultValue(20)]
    [Range(1, 100)]
    public int Limit { get; init; } = 20;

    [FromQuery(Name = "continuationToken")]
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? ContinuationToken { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (To <= From)
        {
            yield return new ValidationResult("To must be later than from.", [nameof(To)]);
        }

        if (ContinuationToken is not null && !ScheduleCursor.TryDecode(ContinuationToken, out _))
        {
            yield return new ValidationResult("Invalid continuation token.", [nameof(ContinuationToken)]);
        }
    }
}
