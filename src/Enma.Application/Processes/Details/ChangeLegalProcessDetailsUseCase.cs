using Enma.Application.Authorization;
using Enma.Application.Validation;

namespace Enma.Application.Processes.Details;

public sealed class ChangeLegalProcessDetailsUseCase
{
    private readonly ProcessActionAuthorization _actionAuthorization;
    private readonly ILegalProcessOperationalMutationPersistence _mutationPersistence;

    public ChangeLegalProcessDetailsUseCase(
        ProcessActionAuthorization actionAuthorization,
        ILegalProcessOperationalMutationPersistence mutationPersistence)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);

        _actionAuthorization = actionAuthorization;
        _mutationPersistence = mutationPersistence;
    }

    public async Task<ChangeLegalProcessDetailsResult> ExecuteAsync(
        ChangeLegalProcessDetailsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (await LegalProcessOperationalMutationSupport.AuthorizeActorMembershipAsync(
                _actionAuthorization,
                command.UserId,
                command.OrganizationId,
                cancellationToken) is not Guid actorMembershipId)
        {
            return ChangeLegalProcessDetailsResult.AccessDenied;
        }

        if (command.ProcessId == Guid.Empty)
        {
            return ChangeLegalProcessDetailsResult.NotFound;
        }

        var request = new LegalProcessOperationalMutationPersistenceRequest(
            command.UserId,
            command.OrganizationId,
            actorMembershipId,
            command.ProcessId,
            LegalProcessOperationalMutation.Details);
        LegalProcessOperationalMutationPersistenceResult persistenceResult;

        try
        {
            persistenceResult = await _mutationPersistence.ChangeOperationalAsync(
                request,
                static _ => null,
                state => Decide(request, state, command),
                cancellationToken);
        }
        catch (ArgumentException exception) when (
            exception.ParamName is "processNumber" or "courtOrAuthority")
        {
            throw new RequestValidationException(exception.Message, exception);
        }

        return persistenceResult switch
        {
            LegalProcessOperationalMutationPersistenceResult.AccessDenied =>
                ChangeLegalProcessDetailsResult.AccessDenied,
            LegalProcessOperationalMutationPersistenceResult.NotFound =>
                ChangeLegalProcessDetailsResult.NotFound,
            LegalProcessOperationalMutationPersistenceResult.DuplicateProcessNumber =>
                ChangeLegalProcessDetailsResult.DuplicateProcessNumber,
            LegalProcessOperationalMutationPersistenceResult.Succeeded =>
                ChangeLegalProcessDetailsResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Legal process details persistence returned an invalid result.")
        };
    }

    private LegalProcessOperationalMutationDecision Decide(
        LegalProcessOperationalMutationPersistenceRequest request,
        LegalProcessOperationalMutationLockedState state,
        ChangeLegalProcessDetailsCommand command)
    {
        if (!LegalProcessOperationalMutationSupport.IsActorAllowed(
                _actionAuthorization,
                request,
                state))
        {
            return LegalProcessOperationalMutationDecision.AccessDenied;
        }

        state.LegalProcess.ChangeDetails(
            command.ProcessNumber,
            command.CourtOrAuthority);
        return LegalProcessOperationalMutationDecision.Persist;
    }
}
