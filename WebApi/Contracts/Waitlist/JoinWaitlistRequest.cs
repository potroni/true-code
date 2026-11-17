using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WebApi.Contracts.Waitlist;

[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class JoinWaitlistRequest
{
    [JsonRequired]
    public long ClassId { get; init; }

    [JsonRequired]
    [Range(1, int.MaxValue)]
    public int Seats { get; init; }
}
