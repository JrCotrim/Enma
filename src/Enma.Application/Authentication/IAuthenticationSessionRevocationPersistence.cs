using Enma.Domain.Authentication;

namespace Enma.Application.Authentication;

public interface IAuthenticationSessionRevocationPersistence
{
    Task RevokeAsync(
        AuthenticationSessionSecretHash secretHash,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
