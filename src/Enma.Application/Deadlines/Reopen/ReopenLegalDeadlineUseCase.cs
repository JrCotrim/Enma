using Enma.Application.Authorization;

namespace Enma.Application.Deadlines.Reopen;

public sealed class ReopenLegalDeadlineUseCase
{
    private readonly DeadlineActionAuthorization _actionAuthorization;
    private readonly ILegalDeadlineMutationPersistence _mutationPersistence;

    public ReopenLegalDeadlineUseCase(
        DeadlineActionAuthorization actionAuthorization,
        ILegalDeadlineMutationPersistence mutationPersistence)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);

        _actionAuthorization = actionAuthorization;
        _mutationPersistence = mutationPersistence;
    }

    public async Task<ReopenLegalDeadlineResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        Guid deadlineId,
        CancellationToken cancellationToken = default)
    {
        DeadlineActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                DeadlineAction.Reopen,
                cancellationToken);

        if (authorization == DeadlineActionAuthorizationResult.Denied)
        {
            return ReopenLegalDeadlineResult.AccessDenied;
        }

        if (deadlineId == Guid.Empty)
        {
            return ReopenLegalDeadlineResult.NotFound;
        }

        LegalDeadlineLifecycleMutationPersistenceResult persistenceResult =
            await _mutationPersistence.ReopenAsync(
                deadlineId,
                organizationId,
                cancellationToken);

        return persistenceResult ==
            LegalDeadlineLifecycleMutationPersistenceResult.Succeeded
                ? ReopenLegalDeadlineResult.Succeeded
                : ReopenLegalDeadlineResult.NotFound;
    }
}
