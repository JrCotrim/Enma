using Enma.Application.Authorization;
using Enma.Application.Validation;

namespace Enma.Application.Processes.Update;

public sealed class UpdateLegalProcessUseCase
{
    private readonly ProcessActionAuthorization _actionAuthorization;
    private readonly ILegalProcessMutationPersistence _mutationPersistence;

    public UpdateLegalProcessUseCase(
        ProcessActionAuthorization actionAuthorization,
        ILegalProcessMutationPersistence mutationPersistence)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);

        _actionAuthorization = actionAuthorization;
        _mutationPersistence = mutationPersistence;
    }

    public async Task<UpdateLegalProcessResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        Guid processId,
        string title,
        CancellationToken cancellationToken = default)
    {
        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                userId,
                organizationId,
                ProcessAction.Update,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return UpdateLegalProcessResult.AccessDenied;
        }

        if (processId == Guid.Empty)
        {
            return UpdateLegalProcessResult.NotFound;
        }

        var request = new LegalProcessMutationPersistenceRequest(
            userId,
            organizationId,
            actorMembershipId,
            processId);
        LegalProcessMutationPersistenceResult persistenceResult;

        try
        {
            persistenceResult = await _mutationPersistence.UpdateTitleAsync(
                request,
                state => DecideUpdate(request, state, title),
                cancellationToken);
        }
        catch (ArgumentException exception) when (exception.ParamName == "title")
        {
            throw new RequestValidationException(exception.Message, exception);
        }

        return persistenceResult switch
        {
            LegalProcessMutationPersistenceResult.AccessDenied =>
                UpdateLegalProcessResult.AccessDenied,
            LegalProcessMutationPersistenceResult.NotFound =>
                UpdateLegalProcessResult.NotFound,
            LegalProcessMutationPersistenceResult.Updated =>
                UpdateLegalProcessResult.Updated,
            _ => throw new InvalidOperationException(
                "Legal process mutation persistence returned an invalid result.")
        };
    }

    private LegalProcessMutationDecision DecideUpdate(
        LegalProcessMutationPersistenceRequest request,
        LegalProcessMutationLockedState state,
        string title)
    {
        if (!state.IsOrganizationActive ||
            state.Actor is not { } actor ||
            !actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) ||
            !_actionAuthorization.CanExecute(ProcessAction.Update, actor.Role))
        {
            return LegalProcessMutationDecision.AccessDenied;
        }

        state.LegalProcess.ChangeTitle(title);
        return LegalProcessMutationDecision.Persist;
    }
}
