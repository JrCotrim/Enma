using Enma.Application.Security;
using Enma.Domain.Authentication;
using Enma.Domain.Users;

namespace Enma.Application.Authentication;

public sealed class LoginUseCase
{
    private readonly IAuthenticationIdentityLookup _identityLookup;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILoginDummyPasswordHashProvider _dummyPasswordHashProvider;
    private readonly IAuthenticationSessionHandleService _sessionHandleService;
    private readonly IAuthenticationSessionIssuancePersistence _sessionPersistence;
    private readonly TimeProvider _timeProvider;

    public LoginUseCase(
        IAuthenticationIdentityLookup identityLookup,
        IPasswordHasher passwordHasher,
        ILoginDummyPasswordHashProvider dummyPasswordHashProvider,
        IAuthenticationSessionHandleService sessionHandleService,
        IAuthenticationSessionIssuancePersistence sessionPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(identityLookup);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(dummyPasswordHashProvider);
        ArgumentNullException.ThrowIfNull(sessionHandleService);
        ArgumentNullException.ThrowIfNull(sessionPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _identityLookup = identityLookup;
        _passwordHasher = passwordHasher;
        _dummyPasswordHashProvider = dummyPasswordHashProvider;
        _sessionHandleService = sessionHandleService;
        _sessionPersistence = sessionPersistence;
        _timeProvider = timeProvider;
    }

    public async Task<LoginResult> ExecuteAsync(
        string? email,
        string? password,
        CancellationToken cancellationToken = default)
    {
        if (password is null)
        {
            return LoginResult.InvalidCredentials;
        }

        string normalizedEmail;

        try
        {
            normalizedEmail = User.NormalizeEmail(email ?? string.Empty);
        }
        catch (ArgumentException)
        {
            return LoginResult.InvalidCredentials;
        }

        AuthenticationIdentity? identity =
            await _identityLookup.FindByNormalizedEmailAsync(
                normalizedEmail,
                AuthenticationIdentityLoadMode.ReadOnly,
                cancellationToken);
        UserCredential? credential = identity?.Credential;

        if (identity is null || credential is null)
        {
            _ = _passwordHasher.VerifyHashedPassword(
                _dummyPasswordHashProvider.PasswordHash,
                password);
            return LoginResult.InvalidCredentials;
        }

        PasswordVerificationResult passwordVerification =
            _passwordHasher.VerifyHashedPassword(
                credential.PasswordHash,
                password);

        if (passwordVerification is not PasswordVerificationResult.Success and
            not PasswordVerificationResult.SuccessRehashNeeded)
        {
            return LoginResult.InvalidCredentials;
        }

        if (!identity.IsActive || identity.EmailVerifiedAt is null)
        {
            return LoginResult.InvalidCredentials;
        }

        string? upgradedPasswordHash =
            passwordVerification == PasswordVerificationResult.SuccessRehashNeeded
                ? _passwordHasher.HashPassword(password)
                : null;
        string rawHandle = _sessionHandleService.GenerateHandle(out var secretHash);
        DateTimeOffset createdAt = _timeProvider.GetUtcNow();
        var session = new AuthenticationSession(
            identity.UserId,
            secretHash,
            credential.CredentialVersion,
            createdAt,
            createdAt.Add(AuthenticationSessionPolicy.IdleLifetime),
            createdAt.Add(AuthenticationSessionPolicy.AbsoluteLifetime));

        AuthenticationSessionIssuancePersistenceResult persistenceResult =
            await _sessionPersistence.TryPersistAsync(
                session,
                upgradedPasswordHash,
                cancellationToken);

        return persistenceResult ==
            AuthenticationSessionIssuancePersistenceResult.Succeeded
                ? LoginResult.Success(rawHandle)
                : LoginResult.InvalidCredentials;
    }
}
