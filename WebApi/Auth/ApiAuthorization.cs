using System.Globalization;
using System.Security.Claims;

namespace WebApi.Auth;

public static class ApiAuthorization
{
    public const string AdminPolicy = "Admin";
    public const string AdminRole = "admin";

    internal static bool TryGetUserId(ClaimsPrincipal principal, out long userId) =>
        long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),
            NumberStyles.None, CultureInfo.InvariantCulture, out userId) && userId > 0;
}
