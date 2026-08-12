using System.Security.Claims;

namespace Enma.Api.Authentication;

internal static class AuthenticatedUserId
{
    internal static bool TryGet(
        ClaimsPrincipal principal,
        out Guid userId)
    {
        ArgumentNullException.ThrowIfNull(principal);

        userId = Guid.Empty;
        Claim[] identifierClaims = principal
            .FindAll(ClaimTypes.NameIdentifier)
            .Take(2)
            .ToArray();

        return identifierClaims.Length == 1 &&
            Guid.TryParseExact(identifierClaims[0].Value, "D", out userId) &&
            userId != Guid.Empty;
    }
}
