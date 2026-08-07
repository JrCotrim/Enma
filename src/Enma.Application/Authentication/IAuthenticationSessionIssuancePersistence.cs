using Enma.Domain.Authentication;

namespace Enma.Application.Authentication;

public interface IAuthenticationSessionIssuancePersistence
{
    Task<AuthenticationSessionIssuancePersistenceResult> TryPersistAsync(
        AuthenticationSession session,
        string? upgradedPasswordHash,
        CancellationToken cancellationToken = default);
}
