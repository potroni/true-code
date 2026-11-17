namespace WebApi.Contracts.Waitlist;

public sealed record JoinWaitlistResponse(
    long Id,
    long ClassId,
    int Seats,
    int Position,
    DateTimeOffset CreatedAt);
