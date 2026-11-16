using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WebApi.Contracts;

[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class JoinWaitlistRequest
{
    [JsonRequired]
    [Range(typeof(long), "1", "9223372036854775807")]
    public long ClassId { get; init; }

    [JsonRequired]
    [Range(1, int.MaxValue)]
    public int Seats { get; init; }
}
