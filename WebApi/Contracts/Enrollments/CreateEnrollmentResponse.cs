namespace WebApi.Contracts.Enrollments;

public sealed record CreateEnrollmentResponse(
    long Id,
    long ClassId,
    int Seats,
    string Status,
    DateTimeOffset CreatedAt);
