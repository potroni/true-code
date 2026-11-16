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
    Task<EnrollmentDetailsResponse?> GetDetailsAsync(long enrollmentId, long userId, CancellationToken ct);

    Task<ChangeSeatsResult> ChangeSeatsAsync(long enrollmentId, long userId, int seats, CancellationToken ct);

    Task<CancelEnrollmentResult> CancelAsync(long enrollmentId, long userId, CancellationToken ct);

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

    public async Task<CancelEnrollmentResult> CancelAsync(long enrollmentId, long userId, CancellationToken ct)
    {
        await using var transaction = await unitOfWork.DbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var classId = await unitOfWork.Enrollment.FindOwnedClassIdAsync(enrollmentId, userId, ct);
        if (classId is null)
            return new CancelEnrollmentResult.NotFound();

        var activity = await unitOfWork.Class.LockByIdAsync(classId.Value, ct);
        if (activity is null)
            return new CancelEnrollmentResult.NotFound();

        var enrollment = await unitOfWork.Enrollment.FindOwnedAsync(enrollmentId, userId, ct);
        if (enrollment is null || enrollment.ClassId != activity.Id)
            return new CancelEnrollmentResult.NotFound();
        if (enrollment.Status == "cancelled")
            return new CancelEnrollmentResult.NotImplemented();

        // решение: для отмененного клубом занятия очередь не продвигаем и блокируем только абонемент владельца.
        var waiting = activity.IsCancelled ? [] : await unitOfWork.Waitlist.GetWaitingAsync(activity.Id, ct);
        var passUserIds = waiting.Select(x => x.UserId).Prepend(userId).Distinct().ToArray();
        await unitOfWork.Pass.LockByUsersAsync(passUserIds, ct);

        if (!activity.IsCancelled && HasStarted(activity))
            return new CancelEnrollmentResult.Conflict("class-started");

        return new CancelEnrollmentResult.NotImplemented();
    }

    public async Task<EnrollmentDetailsResponse?> GetDetailsAsync(
        long enrollmentId, long userId, CancellationToken ct)
    {
        var details = await unitOfWork.Enrollment.GetDetailsAsync(enrollmentId, userId, ct);
        if (details is null)
            return null;

        return new EnrollmentDetailsResponse(
            details.Id,
            details.Status,
            details.Seats,
            details.CreatedAt,
            new EnrollmentClassResponse(
                details.ClassId,
                details.ClassTitle,
                details.StartsAt,
                details.DurationMinutes,
                details.IsClassCancelled,
                new EnrollmentRoomResponse(details.RoomId, details.RoomName)),
            new EnrollmentPassResponse(details.RemainingVisits, details.ValidUntil));
    }

    public async Task<ChangeSeatsResult> ChangeSeatsAsync(
        long enrollmentId, long userId, int seats, CancellationToken ct)
    {
        var requestedAt = timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = await unitOfWork.DbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var classId = await unitOfWork.Enrollment.FindOwnedClassIdAsync(enrollmentId, userId, ct);
        if (classId is null)
            return new ChangeSeatsResult.NotFound();

        var activity = await unitOfWork.Class.LockByIdAsync(classId.Value, ct);
        if (activity is null)
            return new ChangeSeatsResult.NotFound();

        var enrollment = await unitOfWork.Enrollment.FindOwnedAsync(enrollmentId, userId, ct);
        if (enrollment is null || enrollment.ClassId != activity.Id)
            return new ChangeSeatsResult.NotFound();
        if (enrollment.Status == "cancelled")
            return new ChangeSeatsResult.Conflict("enrollment-cancelled");
        if (activity.IsCancelled)
            return new ChangeSeatsResult.Conflict("class-cancelled");
        if (HasStarted(activity))
            return new ChangeSeatsResult.Conflict("class-started");

        var seatDelta = seats - enrollment.Seats;
        if (seatDelta != 0)
        {
            var pass = await LockPassAsync(userId, ct);
            if (HasStarted(activity))
                return new ChangeSeatsResult.Conflict("class-started");

            if (seatDelta > 0)
            {
                var reason = await GetAdditionalSeatsConflictAsync(activity, pass, seatDelta, requestedAt, ct);
                if (reason is not null)
                    return new ChangeSeatsResult.Conflict(reason);
            }

            var remainingVisits = (long)pass.RemainingVisits - seatDelta;
            // решение: отклоняем возврат целиком, если новый остаток не помещается в int.
            if (remainingVisits > int.MaxValue)
                return new ChangeSeatsResult.Conflict("pass-balance-overflow");

            await SaveSeatChangeAsync(enrollment, pass, seats, (int)remainingVisits, ct);
        }

        var details = await GetDetailsAsync(enrollmentId, userId, ct)
            ?? throw new InvalidOperationException("The locked enrollment must have details.");
        if (HasStarted(activity))
            return new ChangeSeatsResult.Conflict("class-started");

        await transaction.CommitAsync(ct);
        return new ChangeSeatsResult.Success(details);
    }

    private async Task SaveSeatChangeAsync(
        Enrollment enrollment, Pass pass, int seats, int remainingVisits, CancellationToken ct)
    {
        var enrollmentEntry = unitOfWork.DbContext.Attach(enrollment);
        var passEntry = unitOfWork.DbContext.Attach(pass);
        try
        {
            enrollment.Seats = seats;
            pass.RemainingVisits = remainingVisits;
            await unitOfWork.SaveChangesAsync(ct);
        }
        finally
        {
            enrollmentEntry.State = EntityState.Detached;
            passEntry.State = EntityState.Detached;
        }
    }

    public async Task<CreateEnrollmentResult> CreateAsync(
        long userId, long classId, int seats, string idempotencyKey, CancellationToken ct)
    {
        var requestedAt = timeProvider.GetUtcNow().UtcDateTime;
        await using var transaction = await unitOfWork.DbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var journal = unitOfWork.Idempotency;
        await journal.LockAsync(userId, Operation, idempotencyKey, ct);
        var lookup = await FindJournalEntryAsync();
        if (lookup.Status != IdempotencyLookupStatus.Missing)
            return ToExistingResult(lookup);

        var (pass, rejection) = await CheckAvailabilityUnderLocksAsync(userId, classId, seats, requestedAt, ct);
        if (rejection is not null)
            return rejection;

        var responseBody = await SaveEnrollmentAsync(pass!, userId, classId, seats, requestedAt, ct);
        journal.AddSuccess(userId, Operation, idempotencyKey, classId, seats, responseBody);
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
            return ToExistingResult(await FindJournalEntryAsync());
        }

        await transaction.CommitAsync(ct);
        return new CreateEnrollmentResult.Success(responseBody);

        Task<IdempotencyLookup> FindJournalEntryAsync() =>
            journal.FindAsync(userId, Operation, idempotencyKey, classId, seats, ct);
    }

    private async Task<(Pass? Pass, CreateEnrollmentResult? Rejection)> CheckAvailabilityUnderLocksAsync(
        long userId, long classId, int seats, DateTime requestedAt, CancellationToken ct)
    {
        var activity = await unitOfWork.Class.LockByIdAsync(classId, ct);
        if (activity is null)
            return (null, new CreateEnrollmentResult.ClassNotFound());
        if (activity.IsCancelled)
            return (null, new CreateEnrollmentResult.Conflict("class-cancelled"));

        var pass = await LockPassAsync(userId, ct);
        var reason = await GetAdditionalSeatsConflictAsync(activity, pass, seats, requestedAt, ct);
        return reason is null ? (pass, null) : (null, new CreateEnrollmentResult.Conflict(reason));
    }

    private async Task<Pass> LockPassAsync(long userId, CancellationToken ct) =>
        (await unitOfWork.Pass.LockByUsersAsync([userId], ct)).Single();

    private bool HasStarted(Class activity) => activity.StartsAt <= timeProvider.GetUtcNow().UtcDateTime;

    private async Task<string?> GetAdditionalSeatsConflictAsync(
        Class activity, Pass pass, int additionalSeats, DateTime requestedAt, CancellationToken ct)
    {
        // решение: считаем только активные брони, поскольку заявки очереди не резервируют места.
        var occupiedSeats = await unitOfWork.Enrollment.SumActiveSeatsAsync(activity.Id, ct);
        if (HasStarted(activity))
            return "class-started";
        if (activity.Capacity - occupiedSeats < additionalSeats)
            return "capacity-exceeded";

        if (pass.RemainingVisits < additionalSeats)
            return "pass-exhausted";
        if (pass.ValidUntil < DateOnly.FromDateTime(requestedAt))
            return "pass-expired";

        return null;
    }

    private async Task<string> SaveEnrollmentAsync(
        Pass pass, long userId, long classId, int seats, DateTime requestedAt, CancellationToken ct)
    {
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
        return JsonSerializer.Serialize(response, JsonSerializerOptions.Web);
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

public abstract record ChangeSeatsResult
{
    private ChangeSeatsResult() { }

    public sealed record Success(EnrollmentDetailsResponse Details) : ChangeSeatsResult;

    public sealed record NotFound : ChangeSeatsResult;

    public sealed record Conflict(string Reason) : ChangeSeatsResult;
}

public abstract record CancelEnrollmentResult
{
    private CancelEnrollmentResult() { }

    public sealed record Success(CancelEnrollmentResponse Response) : CancelEnrollmentResult;

    public sealed record NotFound : CancelEnrollmentResult;

    public sealed record Conflict(string Reason) : CancelEnrollmentResult;

    public sealed record NotImplemented : CancelEnrollmentResult;
}
