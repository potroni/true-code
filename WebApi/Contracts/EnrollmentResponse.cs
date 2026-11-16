namespace WebApi.Contracts;

public sealed record EnrollmentResponse(
    long Id,
    long ClassId,
    int Seats,
    string Status,
    DateTimeOffset CreatedAt);
