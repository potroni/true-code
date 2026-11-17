using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WebApi.Contracts.Enrollments;

[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class ChangeEnrollmentSeatsRequest
{
    [JsonRequired]
    [Range(1, int.MaxValue)]
    public int Seats { get; init; }
}
