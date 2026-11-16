using System.Data;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts;

namespace WebApi.Services;

public interface IEnrollmentService
{
    Task<CreateEnrollmentResult> CreateAsync(
        long userId, long classId, int seats, string idempotencyKey, CancellationToken ct);
}

public abstract record CreateEnrollmentResult
{
    private CreateEnrollmentResult() { }

    public sealed record Success(string ResponseBody) : CreateEnrollmentResult;

    public sealed record ClassNotFound : CreateEnrollmentResult;

    public sealed record Conflict(string Reason) : CreateEnrollmentResult;
}

internal sealed class EnrollmentService(IUnitOfWork unitOfWork, TimeProvider timeProvider) : IEnrollmentService
{
    private const IdempotencyOperation Operation = IdempotencyOperation.CreateEnrollment;

    public async Task<CreateEnrollmentResult> CreateAsync(
        long userId, long classId, int seats, string idempotencyKey, CancellationToken ct)
    {
        var requestedAt = timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = await unitOfWork.DbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var journal = unitOfWork.Idempotency;
        await journal.LockAsync(userId, Operation, idempotencyKey, ct);
        var lookup = await journal.FindAsync(userId, Operation, idempotencyKey, classId, seats, ct);
        if (lookup.Status != IdempotencyLookupStatus.Missing)
            return ToExistingResult(lookup);

        var result = await CreateUnderLocksAsync(userId, classId, seats, requestedAt, ct);
        if (result is not CreateEnrollmentResult.Success success)
            return result;

        journal.AddSuccess(userId, Operation, idempotencyKey, classId, seats, success.ResponseBody);
        try
        {
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ux_idempotency_entries_user_operation_hash"
        })
        {
            await transaction.RollbackAsync(ct);
            unitOfWork.DbContext.ChangeTracker.Clear();

            var winner = await journal.FindAsync(userId, Operation, idempotencyKey, classId, seats, ct);
            return ToExistingResult(winner);
        }

        await transaction.CommitAsync(ct);
        return success;
    }

    private async Task<CreateEnrollmentResult> CreateUnderLocksAsync(
        long userId, long classId, int seats, DateTime requestedAt, CancellationToken ct)
    {
        var activity = await unitOfWork.Class.LockByIdAsync(classId, ct);
        if (activity is null)
            return new CreateEnrollmentResult.ClassNotFound();
        if (activity.IsCancelled)
            return new CreateEnrollmentResult.Conflict("class-cancelled");

        var pass = (await unitOfWork.Pass.LockByUsersAsync([userId], ct)).Single();
        if (activity.StartsAt <= timeProvider.GetUtcNow().UtcDateTime)
            return new CreateEnrollmentResult.Conflict("class-started");

        // решение: очередь не резервирует места, чтобы доступность обычной брони определялась активными бронями.
        var occupiedSeats = await unitOfWork.Enrollment.SumActiveSeatsAsync(classId, ct);
        if (activity.Capacity - occupiedSeats < seats)
            return new CreateEnrollmentResult.Conflict("capacity-exceeded");

        if (pass.RemainingVisits < seats)
            return new CreateEnrollmentResult.Conflict("pass-exhausted");
        if (pass.ValidUntil < DateOnly.FromDateTime(requestedAt))
            return new CreateEnrollmentResult.Conflict("pass-expired");

        var enrollment = new Enrollment
        {
            UserId = userId,
            ClassId = classId,
            Seats = seats,
            Status = "active",
            // решение: усекаем время до микросекунд для совпадения ответа со значением из PostgreSQL.
            CreatedAt = requestedAt.AddTicks(-(requestedAt.Ticks % TimeSpan.TicksPerMicrosecond))
        };
        await unitOfWork.Enrollment.CreateAsync(enrollment, ct);
        unitOfWork.DbContext.Attach(pass);
        pass.RemainingVisits -= seats;
        await unitOfWork.SaveChangesAsync(ct);

        var response = new EnrollmentResponse(
            enrollment.Id, enrollment.ClassId, enrollment.Seats, enrollment.Status, enrollment.CreatedAt);
        return new CreateEnrollmentResult.Success(JsonSerializer.Serialize(response, JsonSerializerOptions.Web));
    }

    private static CreateEnrollmentResult ToExistingResult(IdempotencyLookup lookup) => lookup.Status switch
    {
        IdempotencyLookupStatus.Replay when lookup.ResponseBody is { } body =>
            new CreateEnrollmentResult.Success(body),
        IdempotencyLookupStatus.ParameterConflict =>
            new CreateEnrollmentResult.Conflict("idempotency-key-conflict"),
        _ => throw new InvalidOperationException("Unexpected idempotency lookup result.")
    };
}
