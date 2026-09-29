using Enma.Application.Authorization;
using Enma.Domain.Processes;

namespace Enma.Application.Processes.Status;

public sealed class ChangeLegalProcessStatusUseCase
{
    private readonly ProcessActionAuthorization _actionAuthorization;
    private readonly ILegalProcessOperationalMutationPersistence _mutationPersistence;

    public ChangeLegalProcessStatusUseCase(
        ProcessActionAuthorization actionAuthorization,
        ILegalProcessOperationalMutationPersistence mutationPersistence)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);

        _actionAuthorization = actionAuthorization;
        _mutationPersistence = mutationPersistence;
    }

    public async Task<ChangeLegalProcessStatusResult> ExecuteAsync(
        ChangeLegalProcessStatusCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        LegalProcessStatus status = LegalProcessStatusParser.Parse(command.Status);

        if (await LegalProcessOperationalMutationSupport.AuthorizeActorMembershipAsync(
                _actionAuthorization,
                command.UserId,
                command.OrganizationId,
                cancellationToken) is not Guid actorMembershipId)
        {
            return ChangeLegalProcessStatusResult.AccessDenied;
        }

        if (command.ProcessId == Guid.Empty)
        {
            return ChangeLegalProcessStatusResult.NotFound;
        }

        var request = new LegalProcessOperationalMutationPersistenceRequest(
            command.UserId,
            command.OrganizationId,
            actorMembershipId,
            command.ProcessId,
            LegalProcessOperationalMutation.Status);
        LegalProcessOperationalMutationPersistenceResult persistenceResult =
            await _mutationPersistence.ChangeOperationalAsync(
                request,
                legalProcess => IsReopening(legalProcess, status)
                    ? legalProcess.ResponsibleMembershipId
                    : null,
                state => Decide(request, state, status),
                cancellationToken);

        return persistenceResult switch
        {
            LegalProcessOperationalMutationPersistenceResult.AccessDenied =>
                ChangeLegalProcessStatusResult.AccessDenied,
            LegalProcessOperationalMutationPersistenceResult.NotFound =>
                ChangeLegalProcessStatusResult.NotFound,
            LegalProcessOperationalMutationPersistenceResult.StatusTransitionNotAllowed =>
                ChangeLegalProcessStatusResult.StatusTransitionNotAllowed,
            LegalProcessOperationalMutationPersistenceResult.CurrentResponsibleUnavailable =>
                ChangeLegalProcessStatusResult.CurrentResponsibleUnavailable,
            LegalProcessOperationalMutationPersistenceResult.Succeeded =>
                ChangeLegalProcessStatusResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Legal process status persistence returned an invalid result.")
        };
    }

    private LegalProcessOperationalMutationDecision Decide(
        LegalProcessOperationalMutationPersistenceRequest request,
        LegalProcessOperationalMutationLockedState state,
        LegalProcessStatus status)
    {
        if (!LegalProcessOperationalMutationSupport.IsActorAllowed(
                _actionAuthorization,
                request,
                state))
        {
            return LegalProcessOperationalMutationDecision.AccessDenied;
        }

        LegalProcess legalProcess = state.LegalProcess;

        if (!legalProcess.CanChangeStatusTo(status))
        {
            return LegalProcessOperationalMutationDecision.StatusTransitionNotAllowed;
        }

        if (IsReopening(legalProcess, status) &&
            legalProcess.ResponsibleMembershipId is Guid responsibleMembershipId &&
            !LegalProcessOperationalMutationSupport.IsRelatedMemberAvailable(
                state,
                request.OrganizationId,
                responsibleMembershipId))
        {
            return LegalProcessOperationalMutationDecision.CurrentResponsibleUnavailable;
        }

        legalProcess.ChangeStatus(status);
        return LegalProcessOperationalMutationDecision.Persist;
    }

    private static bool IsReopening(
        LegalProcess legalProcess,
        LegalProcessStatus status)
    {
        return legalProcess.Status == LegalProcessStatus.Closed &&
            status == LegalProcessStatus.InProgress;
    }
}
