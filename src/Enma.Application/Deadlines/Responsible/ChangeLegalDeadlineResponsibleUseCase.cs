using Enma.Application.Authorization;
using Enma.Domain.Deadlines;

namespace Enma.Application.Deadlines.Responsible;

public sealed class ChangeLegalDeadlineResponsibleUseCase
{
    private readonly DeadlineActionAuthorization _actionAuthorization;
    private readonly ILegalDeadlineMutationPersistence _mutationPersistence;

    public ChangeLegalDeadlineResponsibleUseCase(
        DeadlineActionAuthorization actionAuthorization,
        ILegalDeadlineMutationPersistence mutationPersistence)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);

        _actionAuthorization = actionAuthorization;
        _mutationPersistence = mutationPersistence;
    }

    public async Task<ChangeLegalDeadlineResponsibleResult> ExecuteAsync(
        ChangeLegalDeadlineResponsibleCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                command.UserId,
                command.OrganizationId,
                DeadlineAction.Update,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return ChangeLegalDeadlineResponsibleResult.AccessDenied;
        }

        if (command.DeadlineId == Guid.Empty)
        {
            return ChangeLegalDeadlineResponsibleResult.NotFound;
        }

        if (command.ResponsibleMembershipId == Guid.Empty)
        {
            return ChangeLegalDeadlineResponsibleResult.InvalidInput;
        }

        var request = new LegalDeadlineMutationPersistenceRequest(
            command.UserId,
            command.OrganizationId,
            actorMembershipId,
            command.DeadlineId);
        LegalDeadlineResponsibleMutationPersistenceResult persistenceResult =
            await _mutationPersistence.ChangeResponsibleAsync(
                request,
                legalDeadline => command.ResponsibleMembershipId is Guid requestedId &&
                    requestedId != legalDeadline.ResponsibleMembershipId
                        ? requestedId
                        : null,
                state => Decide(request, state, command.ResponsibleMembershipId),
                cancellationToken);

        return persistenceResult switch
        {
            LegalDeadlineResponsibleMutationPersistenceResult.AccessDenied =>
                ChangeLegalDeadlineResponsibleResult.AccessDenied,
            LegalDeadlineResponsibleMutationPersistenceResult.NotFound =>
                ChangeLegalDeadlineResponsibleResult.NotFound,
            LegalDeadlineResponsibleMutationPersistenceResult
                .RelatedResponsibleUnavailable =>
                ChangeLegalDeadlineResponsibleResult.RelatedResponsibleUnavailable,
            LegalDeadlineResponsibleMutationPersistenceResult.Succeeded =>
                ChangeLegalDeadlineResponsibleResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Legal deadline responsible persistence returned an invalid result.")
        };
    }

    private LegalDeadlineMutationDecision Decide(
        LegalDeadlineMutationPersistenceRequest request,
        LegalDeadlineMutationLockedState state,
        Guid? responsibleMembershipId)
    {
        if (!state.IsOrganizationActive ||
            state.Actor is not { } actor ||
            !actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) ||
            !_actionAuthorization.CanExecute(DeadlineAction.Update, actor.Role))
        {
            return LegalDeadlineMutationDecision.AccessDenied;
        }

        LegalDeadline legalDeadline = state.LegalDeadline;

        if (responsibleMembershipId is Guid requestedId &&
            requestedId != legalDeadline.ResponsibleMembershipId &&
            state.RelatedMember?.IsAvailableMemberOf(
                request.OrganizationId,
                requestedId) != true)
        {
            return LegalDeadlineMutationDecision.RelatedResponsibleUnavailable;
        }

        legalDeadline.ChangeResponsible(responsibleMembershipId);
        return LegalDeadlineMutationDecision.Persist;
    }
}
