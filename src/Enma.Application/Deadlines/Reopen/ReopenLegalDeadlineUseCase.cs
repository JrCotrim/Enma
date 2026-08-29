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
        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                userId,
                organizationId,
                DeadlineAction.Reopen,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return ReopenLegalDeadlineResult.AccessDenied;
        }

        if (deadlineId == Guid.Empty)
        {
            return ReopenLegalDeadlineResult.NotFound;
        }

        var request = new LegalDeadlineMutationPersistenceRequest(
            userId,
            organizationId,
            actorMembershipId,
            deadlineId);
        LegalDeadlineLifecycleMutationPersistenceResult persistenceResult =
            await _mutationPersistence.ReopenAsync(
                request,
                state => DecideReopening(request, state),
                cancellationToken);

        return persistenceResult switch
        {
            LegalDeadlineLifecycleMutationPersistenceResult.AccessDenied =>
                ReopenLegalDeadlineResult.AccessDenied,
            LegalDeadlineLifecycleMutationPersistenceResult.NotFound =>
                ReopenLegalDeadlineResult.NotFound,
            LegalDeadlineLifecycleMutationPersistenceResult.Succeeded =>
                ReopenLegalDeadlineResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Legal deadline mutation persistence returned an invalid result.")
        };
    }

    private LegalDeadlineMutationDecision DecideReopening(
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
                DeadlineAction.Reopen,
                actor.Role))
        {
            return LegalDeadlineMutationDecision.AccessDenied;
        }

        state.LegalDeadline.Reopen();
        return LegalDeadlineMutationDecision.Persist;
    }
}
