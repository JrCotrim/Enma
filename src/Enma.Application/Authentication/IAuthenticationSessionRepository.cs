using Enma.Domain.Authentication;

namespace Enma.Application.Authentication;

public interface IAuthenticationSessionRepository
{
    Task<AuthenticationSession?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<AuthenticationSession?> GetBySecretHashAsync(
        AuthenticationSessionSecretHash secretHash,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        AuthenticationSession session,
        CancellationToken cancellationToken = default);
}
