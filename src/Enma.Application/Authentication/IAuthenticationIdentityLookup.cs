namespace Enma.Application.Authentication;

public interface IAuthenticationIdentityLookup
{
    Task<AuthenticationIdentity?> FindByNormalizedEmailAsync(
        string normalizedEmail,
        AuthenticationIdentityLoadMode loadMode,
        CancellationToken cancellationToken = default);
}
