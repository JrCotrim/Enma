using Enma.Application.Security;

namespace Enma.Application.Authentication;

public sealed class LoginDummyPasswordHashProvider
    : ILoginDummyPasswordHashProvider
{
    public LoginDummyPasswordHashProvider(IPasswordHasher passwordHasher)
    {
        ArgumentNullException.ThrowIfNull(passwordHasher);

        PasswordHash = passwordHasher.HashPassword(
            Guid.NewGuid().ToString("N"));
    }

    public string PasswordHash { get; }
}
