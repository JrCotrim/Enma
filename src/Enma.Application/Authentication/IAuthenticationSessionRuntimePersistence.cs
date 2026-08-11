using Enma.Domain.Authentication;

namespace Enma.Application.Authentication;

public interface IAuthenticationSessionRuntimePersistence
{
    Task<Guid?> TryValidateAndRenewAsync(
        AuthenticationSessionSecretHash secretHash,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
