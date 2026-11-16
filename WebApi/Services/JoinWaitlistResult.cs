namespace WebApi.Services;

public abstract record JoinWaitlistResult
{
    private JoinWaitlistResult() { }

    public sealed record Success(string ResponseBody) : JoinWaitlistResult;

    public sealed record ClassNotFound : JoinWaitlistResult;

    public sealed record Conflict(string Reason) : JoinWaitlistResult;

    public sealed record PendingImplementation : JoinWaitlistResult;
}
