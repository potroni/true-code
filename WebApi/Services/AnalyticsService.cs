using DataAccessLayer.UnitOfWork;
using WebApi.Contracts.Analytics;

namespace WebApi.Services;

public interface IAnalyticsService
{
    Task<RoomLoadResponse> GetLoadAsync(RoomLoadQuery query, CancellationToken ct);
}

internal sealed class AnalyticsService(IUnitOfWork unitOfWork) : IAnalyticsService
{
    public async Task<RoomLoadResponse> GetLoadAsync(RoomLoadQuery query, CancellationToken ct)
    {
        var rows = await unitOfWork.Class.GetLoadRowsAsync(query.From, query.To, query.RoomId, ct);
        var items = rows.Select(row => new LoadItemResponse(row.Date, new LoadRoomResponse(row.RoomId, row.RoomName),
            row.ClassesCount, row.TotalCapacity, row.BookedSeats)).ToArray();
        return new RoomLoadResponse(items);
    }
}
