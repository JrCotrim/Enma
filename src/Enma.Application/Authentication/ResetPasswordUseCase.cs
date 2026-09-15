using Enma.Application.Security;
using Enma.Application.Validation;

namespace Enma.Application.Authentication;

public sealed class ResetPasswordUseCase
{
    private readonly IPasswordRecoveryTokenService tokenService;
    private readonly IPasswordPolicy passwordPolicy;
    private readonly ICompromisedPasswordChecker compromisedPasswordChecker;
    private readonly IPasswordHasher passwordHasher;
    private readonly IPasswordRecoveryPersistence persistence;

    public ResetPasswordUseCase(
        IPasswordRecoveryTokenService tokenService,
        IPasswordPolicy passwordPolicy,
        ICompromisedPasswordChecker compromisedPasswordChecker,
        IPasswordHasher passwordHasher,
        IPasswordRecoveryPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(tokenService);
        ArgumentNullException.ThrowIfNull(passwordPolicy);
        ArgumentNullException.ThrowIfNull(compromisedPasswordChecker);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(persistence);

        this.tokenService = tokenService;
        this.passwordPolicy = passwordPolicy;
        this.compromisedPasswordChecker = compromisedPasswordChecker;
        this.passwordHasher = passwordHasher;
        this.persistence = persistence;
    }

    public async Task<ResetPasswordResult> ExecuteAsync(
        string? rawToken,
        string? newPassword,
        CancellationToken cancellationToken = default)
    {
        if (!tokenService.TryHashToken(rawToken, out var tokenHash) || tokenHash is null)
        {
            return ResetPasswordResult.Invalid;
        }

        ValidatePassword(newPassword);

        bool compromised = await compromisedPasswordChecker.IsCompromisedAsync(
            newPassword!,
            cancellationToken);

        if (compromised)
        {
            throw new CompromisedPasswordException();
        }

        string passwordHash = passwordHasher.HashPassword(newPassword!);
        PasswordRecoveryResetPersistenceResult result =
            await persistence.TryResetPasswordAsync(
                tokenHash,
                passwordHash,
                cancellationToken);

        return result == PasswordRecoveryResetPersistenceResult.Succeeded
            ? ResetPasswordResult.Succeeded
            : ResetPasswordResult.Invalid;
    }

    private void ValidatePassword(string? password)
    {
        try
        {
            passwordPolicy.Validate(password!);
        }
        catch (ArgumentException exception)
            when (exception.ParamName == "password")
        {
            throw new RequestValidationException(exception.Message, exception);
        }
    }
}
