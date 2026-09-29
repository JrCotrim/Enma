using Enma.Application.Authorization;

namespace Enma.Application.Processes.Responsible;

public sealed class ChangeLegalProcessResponsibleUseCase
{
    private readonly ProcessActionAuthorization _actionAuthorization;
    private readonly ILegalProcessOperationalMutationPersistence _mutationPersistence;

    public ChangeLegalProcessResponsibleUseCase(
        ProcessActionAuthorization actionAuthorization,
        ILegalProcessOperationalMutationPersistence mutationPersistence)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);

        _actionAuthorization = actionAuthorization;
        _mutationPersistence = mutationPersistence;
    }

    public async Task<ChangeLegalProcessResponsibleResult> ExecuteAsync(
        ChangeLegalProcessResponsibleCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (await LegalProcessOperationalMutationSupport.AuthorizeActorMembershipAsync(
                _actionAuthorization,
                command.UserId,
                command.OrganizationId,
                cancellationToken) is not Guid actorMembershipId)
        {
            return ChangeLegalProcessResponsibleResult.AccessDenied;
        }

        if (command.ProcessId == Guid.Empty)
        {
            return ChangeLegalProcessResponsibleResult.NotFound;
        }

        if (command.ResponsibleMembershipId == Guid.Empty)
        {
            return ChangeLegalProcessResponsibleResult.InvalidInput;
        }

        var request = new LegalProcessOperationalMutationPersistenceRequest(
            command.UserId,
            command.OrganizationId,
            actorMembershipId,
            command.ProcessId,
            LegalProcessOperationalMutation.Responsible);
        LegalProcessOperationalMutationPersistenceResult persistenceResult =
            await _mutationPersistence.ChangeOperationalAsync(
                request,
                legalProcess => command.ResponsibleMembershipId is Guid requestedId &&
                    requestedId != legalProcess.ResponsibleMembershipId
                        ? requestedId
                        : null,
                state => Decide(request, state, command.ResponsibleMembershipId),
                cancellationToken);

        return persistenceResult switch
        {
            LegalProcessOperationalMutationPersistenceResult.AccessDenied =>
                ChangeLegalProcessResponsibleResult.AccessDenied,
            LegalProcessOperationalMutationPersistenceResult.NotFound =>
                ChangeLegalProcessResponsibleResult.NotFound,
            LegalProcessOperationalMutationPersistenceResult.RelatedResponsibleUnavailable =>
                ChangeLegalProcessResponsibleResult.RelatedResponsibleUnavailable,
            LegalProcessOperationalMutationPersistenceResult.Succeeded =>
                ChangeLegalProcessResponsibleResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Legal process responsible persistence returned an invalid result.")
        };
    }

    private LegalProcessOperationalMutationDecision Decide(
        LegalProcessOperationalMutationPersistenceRequest request,
        LegalProcessOperationalMutationLockedState state,
        Guid? responsibleMembershipId)
    {
        if (!LegalProcessOperationalMutationSupport.IsActorAllowed(
                _actionAuthorization,
                request,
                state))
        {
            return LegalProcessOperationalMutationDecision.AccessDenied;
        }

        if (responsibleMembershipId is Guid requestedId &&
            requestedId != state.LegalProcess.ResponsibleMembershipId &&
            !LegalProcessOperationalMutationSupport.IsRelatedMemberAvailable(
                state,
                request.OrganizationId,
                requestedId))
        {
            return LegalProcessOperationalMutationDecision.RelatedResponsibleUnavailable;
        }

        state.LegalProcess.ChangeResponsible(responsibleMembershipId);
        return LegalProcessOperationalMutationDecision.Persist;
    }
}
