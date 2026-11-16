using System.Buffers.Binary;
using Microsoft.AspNetCore.WebUtilities;

namespace WebApi.Contracts;

internal readonly record struct ScheduleCursor(DateTime StartsAt, long ClassId)
{
    private const int ByteLength = 2 * sizeof(long);
    private const int TokenLength = 22;

    public string Encode()
    {
        // решение: храним UTC ticks и id в Base64URL, чтобы сохранить точность и передавать токен в URL.
        var bytes = new byte[ByteLength];
        BinaryPrimitives.WriteInt64BigEndian(bytes, StartsAt.Ticks);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(sizeof(long)), ClassId);
        return WebEncoders.Base64UrlEncode(bytes);
    }

    public static bool TryDecode(string? token, out ScheduleCursor cursor)
    {
        cursor = default;
        if (token is not { Length: TokenLength })
            return false;

        byte[] bytes;
        try
        {
            bytes = WebEncoders.Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length != ByteLength || WebEncoders.Base64UrlEncode(bytes) != token)
            return false;

        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
        var classId = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(sizeof(long)));
        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks || classId <= 0)
            return false;

        cursor = new ScheduleCursor(new DateTime(ticks, DateTimeKind.Utc), classId);
        return true;
    }
}
