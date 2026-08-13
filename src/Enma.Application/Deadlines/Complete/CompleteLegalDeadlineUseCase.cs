using Enma.Application.Authorization;

namespace Enma.Application.Deadlines.Complete;

public sealed class CompleteLegalDeadlineUseCase
{
    private readonly DeadlineActionAuthorization _actionAuthorization;
    private readonly ILegalDeadlineMutationPersistence _mutationPersistence;
    private readonly TimeProvider _timeProvider;

    public CompleteLegalDeadlineUseCase(
        DeadlineActionAuthorization actionAuthorization,
        ILegalDeadlineMutationPersistence mutationPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _actionAuthorization = actionAuthorization;
        _mutationPersistence = mutationPersistence;
        _timeProvider = timeProvider;
    }

    public async Task<CompleteLegalDeadlineResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        Guid deadlineId,
        CancellationToken cancellationToken = default)
    {
        DeadlineActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                DeadlineAction.Complete,
                cancellationToken);

        if (authorization == DeadlineActionAuthorizationResult.Denied)
        {
            return CompleteLegalDeadlineResult.AccessDenied;
        }

        if (deadlineId == Guid.Empty)
        {
            return CompleteLegalDeadlineResult.NotFound;
        }

        LegalDeadlineLifecycleMutationPersistenceResult persistenceResult =
            await _mutationPersistence.CompleteAsync(
                deadlineId,
                organizationId,
                _timeProvider.GetUtcNow(),
                cancellationToken);

        return persistenceResult ==
            LegalDeadlineLifecycleMutationPersistenceResult.Succeeded
                ? CompleteLegalDeadlineResult.Succeeded
                : CompleteLegalDeadlineResult.NotFound;
    }
}
