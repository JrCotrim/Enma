using Enma.Domain.Authentication;

namespace Enma.Application.Authentication;

public interface IAuthenticationSessionHandleService
{
    string GenerateHandle(
        out AuthenticationSessionSecretHash secretHash);

    bool TryHashHandle(
        string? rawHandle,
        out AuthenticationSessionSecretHash? secretHash);
}
