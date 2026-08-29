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
        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                userId,
                organizationId,
                DeadlineAction.Complete,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return CompleteLegalDeadlineResult.AccessDenied;
        }

        if (deadlineId == Guid.Empty)
        {
            return CompleteLegalDeadlineResult.NotFound;
        }

        var request = new LegalDeadlineMutationPersistenceRequest(
            userId,
            organizationId,
            actorMembershipId,
            deadlineId);
        LegalDeadlineLifecycleMutationPersistenceResult persistenceResult =
            await _mutationPersistence.CompleteAsync(
                request,
                state => DecideCompletion(request, state),
                cancellationToken);

        return persistenceResult switch
        {
            LegalDeadlineLifecycleMutationPersistenceResult.AccessDenied =>
                CompleteLegalDeadlineResult.AccessDenied,
            LegalDeadlineLifecycleMutationPersistenceResult.NotFound =>
                CompleteLegalDeadlineResult.NotFound,
            LegalDeadlineLifecycleMutationPersistenceResult.Succeeded =>
                CompleteLegalDeadlineResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Legal deadline mutation persistence returned an invalid result.")
        };
    }

    private LegalDeadlineMutationDecision DecideCompletion(
        LegalDeadlineMutationPersistenceRequest request,
        LegalDeadlineMutationLockedState state)
    {
        if (!state.IsOrganizationActive ||
            state.Actor is not { } actor ||
            !actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) ||
            !_actionAuthorization.CanExecute(
                DeadlineAction.Complete,
                actor.Role))
        {
            return LegalDeadlineMutationDecision.AccessDenied;
        }

        state.LegalDeadline.Complete(_timeProvider.GetUtcNow());
        return LegalDeadlineMutationDecision.Persist;
    }
}
