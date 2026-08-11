namespace Enma.Application.Authentication;

public sealed class RevokeSessionUseCase
{
    private readonly IAuthenticationSessionHandleService _sessionHandleService;
    private readonly IAuthenticationSessionRevocationPersistence _sessionPersistence;
    private readonly TimeProvider _timeProvider;

    public RevokeSessionUseCase(
        IAuthenticationSessionHandleService sessionHandleService,
        IAuthenticationSessionRevocationPersistence sessionPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(sessionHandleService);
        ArgumentNullException.ThrowIfNull(sessionPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _sessionHandleService = sessionHandleService;
        _sessionPersistence = sessionPersistence;
        _timeProvider = timeProvider;
    }

    public async Task ExecuteAsync(
        string? rawHandle,
        CancellationToken cancellationToken = default)
    {
        if (!_sessionHandleService.TryHashHandle(rawHandle, out var secretHash) ||
            secretHash is null)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        await _sessionPersistence.RevokeAsync(
            secretHash,
            now,
            cancellationToken);
    }
}
