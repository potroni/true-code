using WebApi.Contracts;

namespace WebApi.Services;

public abstract record CancelWaitlistResult
{
    private CancelWaitlistResult() { }

    public sealed record Success(CancelWaitlistResponse Response) : CancelWaitlistResult;

    public sealed record NotFound : CancelWaitlistResult;

    public sealed record Conflict(string Reason) : CancelWaitlistResult;
}
