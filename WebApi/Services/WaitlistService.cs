using System.Data;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts;

namespace WebApi.Services;

public interface IWaitlistService
{
    Task<JoinWaitlistResult> JoinWaitlistAsync(
        long userId, long classId, int seats, string idempotencyKey, CancellationToken ct);

    Task<CancelWaitlistResult> CancelWaitlistAsync(long id, long userId, CancellationToken ct);
}

internal sealed class WaitlistService(
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    IdempotentWriteExecutor idempotentWriteExecutor) : IWaitlistService
{
    public Task<JoinWaitlistResult> JoinWaitlistAsync(
        long userId, long classId, int seats, string idempotencyKey, CancellationToken ct) =>
        idempotentWriteExecutor.ExecuteAsync<JoinWaitlistResult>(
            userId, IdempotencyOperation.JoinWaitlist, idempotencyKey, classId, seats,
            lookup => lookup.Status switch
            {
                IdempotencyLookupStatus.Replay when lookup.ResponseBody is { } body =>
                    new JoinWaitlistResult.Success(body),
                IdempotencyLookupStatus.ParameterConflict =>
                    new JoinWaitlistResult.Conflict("idempotency-key-conflict"),
                _ => throw new InvalidOperationException("Unexpected idempotency lookup result.")
            },
            () => JoinWaitlistUnderClassLockAsync(userId, classId, seats, ct),
            ct);

    private async Task<(JoinWaitlistResult Result, string? ResponseBody, Func<JoinWaitlistResult?>? ValidateBeforeCommit)>
        JoinWaitlistUnderClassLockAsync(
        long userId, long classId, int seats, CancellationToken ct)
    {
        var activity = await unitOfWork.Class.LockByIdAsync(classId, ct);
        if (activity is null)
            return (new JoinWaitlistResult.ClassNotFound(), null, null);
        if (activity.IsCancelled)
            return (new JoinWaitlistResult.Conflict("class-cancelled"), null, null);
        if (HasStarted(activity))
            return (new JoinWaitlistResult.Conflict("class-started"), null, null);

        // решение: проверяем действующую заявку раньше вместимости, чтобы сообщить о ней даже при свободных местах.
        if (await unitOfWork.Waitlist.HasWaitingAsync(classId, userId, ct))
            return (new JoinWaitlistResult.Conflict("already-waiting"), null, null);
        // решение: отклоняем заявки, которые невозможно продвинуть целиком даже на пустое занятие.
        if (seats > activity.Capacity)
            return (new JoinWaitlistResult.Conflict("seats-exceed-capacity"), null, null);

        var occupiedSeats = await unitOfWork.Enrollment.SumActiveSeatsAsync(classId, ct);
        var createdAt = timeProvider.GetUtcNow().UtcDateTime;
        if (activity.StartsAt <= createdAt)
            return (new JoinWaitlistResult.Conflict("class-started"), null, null);
        if (activity.Capacity - occupiedSeats >= seats)
            return (new JoinWaitlistResult.Conflict("seats-available"), null, null);

        var entry = new WaitlistEntry
        {
            UserId = userId,
            ClassId = classId,
            Seats = seats,
            // решение: усекаем время до микросекунд для совпадения с PostgreSQL при расчете позиции.
            CreatedAt = createdAt.AddTicks(-(createdAt.Ticks % TimeSpan.TicksPerMicrosecond)),
            Status = WaitlistStatus.Waiting
        };
        await unitOfWork.Waitlist.CreateAsync(entry, ct);
        try
        {
            await unitOfWork.SaveChangesAsync(ct);
            var position = 1 + await unitOfWork.Waitlist.CountWaitingAheadAsync(entry, ct);
            var response = new JoinWaitlistResponse(entry.Id, entry.ClassId, entry.Seats, position, entry.CreatedAt);
            var body = JsonSerializer.Serialize(response, JsonSerializerOptions.Web);
            return (new JoinWaitlistResult.Success(body), body, RejectIfStarted);
        }
        finally
        {
            unitOfWork.DbContext.Entry(entry).State = EntityState.Detached;
        }

        JoinWaitlistResult? RejectIfStarted() =>
            HasStarted(activity) ? new JoinWaitlistResult.Conflict("class-started") : null;
    }

    public async Task<CancelWaitlistResult> CancelWaitlistAsync(long id, long userId, CancellationToken ct)
    {
        await using var transaction = await unitOfWork.DbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var classId = await unitOfWork.Waitlist.FindOwnedClassIdAsync(id, userId, ct);
        if (classId is null)
            return new CancelWaitlistResult.NotFound();

        var activity = await unitOfWork.Class.LockByIdAsync(classId.Value, ct);
        if (activity is null)
            return new CancelWaitlistResult.NotFound();

        var entry = await unitOfWork.Waitlist.FindOwnedAsync(id, userId, ct);
        if (entry is null || entry.ClassId != activity.Id)
            return new CancelWaitlistResult.NotFound();

        var success = new CancelWaitlistResult.Success(new CancelWaitlistResponse(entry.Id));
        switch (entry.Status)
        {
            case WaitlistStatus.Cancelled:
                return success;
            // решение: сохраняем итоговый статус, чтобы отличать созданную бронь от завершения без брони.
            case WaitlistStatus.Promoted:
                return new CancelWaitlistResult.Conflict("already-promoted");
            case WaitlistStatus.Closed:
                return new CancelWaitlistResult.Conflict("waitlist-closed");
            case WaitlistStatus.Waiting:
                break;
            default:
                throw new InvalidOperationException("Unexpected waitlist status.");
        }

        var trackedEntry = unitOfWork.DbContext.Attach(entry);
        try
        {
            // решение: выход меняет только статус заявки, продвижение запускается при освобождении мест в брони.
            entry.Status = WaitlistStatus.Cancelled;
            await unitOfWork.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            trackedEntry.State = EntityState.Detached;
        }

        return success;
    }

    private bool HasStarted(Class activity) => activity.StartsAt <= timeProvider.GetUtcNow().UtcDateTime;
}

public abstract record CancelWaitlistResult
{
    private CancelWaitlistResult() { }

    public sealed record Success(CancelWaitlistResponse Response) : CancelWaitlistResult;

    public sealed record NotFound : CancelWaitlistResult;

    public sealed record Conflict(string Reason) : CancelWaitlistResult;
}

public abstract record JoinWaitlistResult
{
    private JoinWaitlistResult() { }

    public sealed record Success(string ResponseBody) : JoinWaitlistResult;

    public sealed record ClassNotFound : JoinWaitlistResult;

    public sealed record Conflict(string Reason) : JoinWaitlistResult;
}
