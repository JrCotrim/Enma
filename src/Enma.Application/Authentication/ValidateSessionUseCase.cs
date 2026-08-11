namespace Enma.Application.Authentication;

public sealed class ValidateSessionUseCase
{
    private readonly IAuthenticationSessionHandleService _sessionHandleService;
    private readonly IAuthenticationSessionRuntimePersistence _sessionPersistence;
    private readonly TimeProvider _timeProvider;

    public ValidateSessionUseCase(
        IAuthenticationSessionHandleService sessionHandleService,
        IAuthenticationSessionRuntimePersistence sessionPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(sessionHandleService);
        ArgumentNullException.ThrowIfNull(sessionPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _sessionHandleService = sessionHandleService;
        _sessionPersistence = sessionPersistence;
        _timeProvider = timeProvider;
    }

    public async Task<SessionValidationResult> ExecuteAsync(
        string? rawHandle,
        CancellationToken cancellationToken = default)
    {
        if (!_sessionHandleService.TryHashHandle(rawHandle, out var secretHash) ||
            secretHash is null)
        {
            return SessionValidationResult.Unauthenticated;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        Guid? userId = await _sessionPersistence.TryValidateAndRenewAsync(
            secretHash,
            now,
            cancellationToken);

        return userId.HasValue
            ? SessionValidationResult.Authenticated(userId.Value)
            : SessionValidationResult.Unauthenticated;
    }
}
