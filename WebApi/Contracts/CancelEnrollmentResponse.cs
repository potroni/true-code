namespace WebApi.Contracts;

public sealed record CancelEnrollmentResponse(long Id, int RefundedVisits)
{
    public string Status => "cancelled";
}
