namespace WebApi.Contracts.Enrollments;

public sealed record CancelEnrollmentResponse(long Id, int RefundedVisits)
{
    public string Status => "cancelled";
}
