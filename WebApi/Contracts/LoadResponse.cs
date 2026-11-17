using System.Text.Json.Serialization;

namespace WebApi.Contracts;

public sealed record LoadResponse([property: JsonRequired] IReadOnlyList<LoadItemResponse> Items);

public sealed record LoadItemResponse(
    DateOnly Date,
    LoadRoomResponse Room,
    int ClassesCount,
    int TotalCapacity,
    int BookedSeats);

public sealed record LoadRoomResponse(long Id, string Name);
