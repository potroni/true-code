using System.Data;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
        var requestedAt = timeProvider.GetUtcNow().UtcDateTime;
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
            return new CancelEnrollmentResult.Success(
                new CancelEnrollmentResponse(enrollment.Id, enrollment.RefundedVisits!.Value));

        var (candidates, availableSeats) = await GetPromotionCandidatesAsync(activity, enrollment.Seats, ct);
        var passUserIds = candidates.Select(x => x.UserId).Prepend(userId).Distinct().ToArray();
        var passes = await unitOfWork.Pass.LockByUsersAsync(passUserIds, ct);

        var cancelledAt = timeProvider.GetUtcNow().UtcDateTime;
        if (!activity.IsCancelled && activity.StartsAt <= cancelledAt)
            return new CancelEnrollmentResult.Conflict("class-started");

        var refundedVisits = activity.IsCancelled || activity.StartsAt - requestedAt >= TimeSpan.FromHours(2)
            ? enrollment.Seats
            : 0;
        var passesByUser = passes.ToDictionary(x => x.UserId);
        var pass = passesByUser[userId];
        var remainingVisits = (long)pass.RemainingVisits + refundedVisits;
        // решение: отклоняем отмену целиком, если полный возврат не помещается в int.
        if (remainingVisits > int.MaxValue)
            return new CancelEnrollmentResult.Conflict("pass-balance-overflow");

        var trackedEntries = new List<EntityEntry>();
        try
        {
            trackedEntries.Add(unitOfWork.DbContext.Attach(enrollment));
            trackedEntries.Add(unitOfWork.DbContext.Attach(pass));
            enrollment.Status = "cancelled";
            enrollment.CancelledAt = cancelledAt;
            enrollment.RefundedVisits = refundedVisits;
            pass.RemainingVisits = (int)remainingVisits;
            await PromoteWaitlistAsync(candidates, passesByUser, availableSeats, cancelledAt, trackedEntries, ct);
            await unitOfWork.SaveChangesAsync(ct);
            if (!activity.IsCancelled && HasStarted(activity))
                return new CancelEnrollmentResult.Conflict("class-started");

            await transaction.CommitAsync(ct);
        }
        finally
        {
            foreach (var entry in trackedEntries)
                entry.State = EntityState.Detached;
        }

        return new CancelEnrollmentResult.Success(new CancelEnrollmentResponse(enrollment.Id, refundedVisits));
    }

    private async Task<(WaitlistEntry[] Candidates, long AvailableSeats)> GetPromotionCandidatesAsync(
        Class activity, int releasedSeats, CancellationToken ct)
    {
        // решение: для отмененного клубом занятия очередь не продвигаем и блокируем только абонемент владельца.
        if (activity.IsCancelled || releasedSeats <= 0)
            return ([], 0);

        var waiting = await unitOfWork.Waitlist.GetWaitingAsync(activity.Id, ct);
        if (waiting.Count == 0)
            return ([], 0);

        var availableSeats = activity.Capacity
            - await unitOfWork.Enrollment.SumActiveSeatsAsync(activity.Id, ct) + releasedSeats;
        // решение: до проверки абонементов не вычитаем места предыдущих заявок, поскольку они могут закрыться.
        var candidates = waiting.TakeWhile(request => request.Seats <= availableSeats).ToArray();
        return (candidates, availableSeats);
    }

    private async Task PromoteWaitlistAsync(
        IReadOnlyList<WaitlistEntry> waiting,
        IReadOnlyDictionary<long, Pass> passesByUser,
        long availableSeats,
        DateTime promotedAt,
        List<EntityEntry> trackedEntries,
        CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(promotedAt);
        foreach (var request in waiting)
        {
            if (request.Seats > availableSeats)
                break;

            var pass = passesByUser[request.UserId];
            trackedEntries.Add(unitOfWork.DbContext.Attach(request));
            if (pass.ValidUntil < today || pass.RemainingVisits < request.Seats)
            {
                request.Status = WaitlistStatus.Closed;
                continue;
            }

            var passEntry = unitOfWork.DbContext.Entry(pass);
            if (passEntry.State == EntityState.Detached)
            {
                passEntry.State = EntityState.Unchanged;
                trackedEntries.Add(passEntry);
            }

            var enrollment = new Enrollment
            {
                UserId = request.UserId,
                ClassId = request.ClassId,
                Seats = request.Seats,
                Status = "active",
                CreatedAt = promotedAt
            };
            trackedEntries.Add(await unitOfWork.DbContext.AddAsync(enrollment, ct));
            pass.RemainingVisits -= request.Seats;
            request.Status = WaitlistStatus.Promoted;
            availableSeats -= request.Seats;
        }
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
            var (candidates, availableSeats) = await GetPromotionCandidatesAsync(activity, -seatDelta, ct);
            var passUserIds = candidates.Select(x => x.UserId).Prepend(userId).Distinct().ToArray();
            var passes = await unitOfWork.Pass.LockByUsersAsync(passUserIds, ct);
            var passesByUser = passes.ToDictionary(x => x.UserId);
            var pass = passesByUser[userId];
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

            var changedAt = timeProvider.GetUtcNow().UtcDateTime;
            if (activity.StartsAt <= changedAt)
                return new ChangeSeatsResult.Conflict("class-started");

            var trackedEntries = new List<EntityEntry>();
            try
            {
                trackedEntries.Add(unitOfWork.DbContext.Attach(enrollment));
                trackedEntries.Add(unitOfWork.DbContext.Attach(pass));
                enrollment.Seats = seats;
                pass.RemainingVisits = (int)remainingVisits;
                await PromoteWaitlistAsync(candidates, passesByUser, availableSeats, changedAt, trackedEntries, ct);
                await unitOfWork.SaveChangesAsync(ct);
            }
            finally
            {
                foreach (var entry in trackedEntries)
                    entry.State = EntityState.Detached;
            }
        }

        var details = await GetDetailsAsync(enrollmentId, userId, ct)
            ?? throw new InvalidOperationException("The locked enrollment must have details.");
        if (HasStarted(activity))
            return new ChangeSeatsResult.Conflict("class-started");

        await transaction.CommitAsync(ct);
        return new ChangeSeatsResult.Success(details);
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
}
