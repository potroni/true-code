using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WebApi.Contracts;

[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class ChangeSeatsRequest
{
    [JsonRequired]
    [Range(1, int.MaxValue)]
    public int Seats { get; init; }
}
