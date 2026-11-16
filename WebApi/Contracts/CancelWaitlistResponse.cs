namespace WebApi.Contracts;

public sealed record CancelWaitlistResponse(long Id)
{
    public string Status => "cancelled";
}
