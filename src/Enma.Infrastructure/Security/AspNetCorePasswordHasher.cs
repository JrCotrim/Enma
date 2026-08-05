using Enma.Application.Security;
using Enma.Domain.Users;
using MicrosoftPasswordHasher = Microsoft.AspNetCore.Identity.IPasswordHasher<Enma.Domain.Users.User>;
using MicrosoftPasswordVerificationResult = Microsoft.AspNetCore.Identity.PasswordVerificationResult;

namespace Enma.Infrastructure.Security;

public sealed class AspNetCorePasswordHasher : IPasswordHasher
{
    private readonly MicrosoftPasswordHasher microsoftHasher;

    public AspNetCorePasswordHasher(MicrosoftPasswordHasher microsoftHasher)
    {
        ArgumentNullException.ThrowIfNull(microsoftHasher);

        this.microsoftHasher = microsoftHasher;
    }

    public string HashPassword(
        User user,
        string password)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(password);

        return microsoftHasher.HashPassword(user, password);
    }

    public PasswordVerificationResult VerifyHashedPassword(
        User user,
        string passwordHash,
        string providedPassword)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(passwordHash);
        ArgumentNullException.ThrowIfNull(providedPassword);

        MicrosoftPasswordVerificationResult result =
            microsoftHasher.VerifyHashedPassword(
                user,
                passwordHash,
                providedPassword);

        return result switch
        {
            MicrosoftPasswordVerificationResult.Failed =>
                PasswordVerificationResult.Failed,
            MicrosoftPasswordVerificationResult.Success =>
                PasswordVerificationResult.Success,
            MicrosoftPasswordVerificationResult.SuccessRehashNeeded =>
                PasswordVerificationResult.SuccessRehashNeeded,
            _ => throw new InvalidOperationException(
                "The password hasher returned an unsupported verification result.")
        };
    }
}
