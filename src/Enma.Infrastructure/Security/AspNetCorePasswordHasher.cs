using Enma.Application.Security;
using MicrosoftPasswordHasher = Microsoft.AspNetCore.Identity.IPasswordHasher<object>;
using MicrosoftPasswordVerificationResult = Microsoft.AspNetCore.Identity.PasswordVerificationResult;

namespace Enma.Infrastructure.Security;

public sealed class AspNetCorePasswordHasher : IPasswordHasher
{
    private static readonly object ProviderUser = new();

    private readonly MicrosoftPasswordHasher microsoftHasher;

    public AspNetCorePasswordHasher(MicrosoftPasswordHasher microsoftHasher)
    {
        ArgumentNullException.ThrowIfNull(microsoftHasher);

        this.microsoftHasher = microsoftHasher;
    }

    public string HashPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        return microsoftHasher.HashPassword(ProviderUser, password);
    }

    public PasswordVerificationResult VerifyHashedPassword(
        string passwordHash,
        string providedPassword)
    {
        ArgumentNullException.ThrowIfNull(passwordHash);
        ArgumentNullException.ThrowIfNull(providedPassword);

        MicrosoftPasswordVerificationResult result =
            microsoftHasher.VerifyHashedPassword(
                ProviderUser,
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
