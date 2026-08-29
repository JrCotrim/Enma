using Enma.Application.Authorization;
using Enma.Application.Validation;

namespace Enma.Application.Clients.Update;

public sealed class UpdateClientUseCase
{
    private readonly ClientActionAuthorization _actionAuthorization;
    private readonly IClientMutationPersistence _mutationPersistence;

    public UpdateClientUseCase(
        ClientActionAuthorization actionAuthorization,
        IClientMutationPersistence mutationPersistence)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);

        _actionAuthorization = actionAuthorization;
        _mutationPersistence = mutationPersistence;
    }

    public async Task<UpdateClientResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        Guid clientId,
        string name,
        CancellationToken cancellationToken = default)
    {
        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                userId,
                organizationId,
                ClientAction.Update,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return UpdateClientResult.AccessDenied;
        }

        if (clientId == Guid.Empty)
        {
            return UpdateClientResult.NotFound;
        }

        var request = new ClientMutationPersistenceRequest(
            userId,
            organizationId,
            actorMembershipId,
            clientId);
        ClientMutationPersistenceResult persistenceResult;

        try
        {
            persistenceResult = await _mutationPersistence.UpdateNameAsync(
                request,
                state => DecideUpdate(request, state, name),
                cancellationToken);
        }
        catch (ArgumentException exception) when (exception.ParamName == "name")
        {
            throw new RequestValidationException(exception.Message, exception);
        }

        return persistenceResult switch
        {
            ClientMutationPersistenceResult.AccessDenied =>
                UpdateClientResult.AccessDenied,
            ClientMutationPersistenceResult.NotFound =>
                UpdateClientResult.NotFound,
            ClientMutationPersistenceResult.Succeeded =>
                UpdateClientResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Client mutation persistence returned an invalid result.")
        };
    }

    private ClientMutationDecision DecideUpdate(
        ClientMutationPersistenceRequest request,
        ClientMutationLockedState state,
        string name)
    {
        if (!IsAuthorized(request, state, ClientAction.Update))
        {
            return ClientMutationDecision.AccessDenied;
        }

        state.Client.ChangeName(name);
        return ClientMutationDecision.Persist;
    }

    private bool IsAuthorized(
        ClientMutationPersistenceRequest request,
        ClientMutationLockedState state,
        ClientAction action)
    {
        return state.IsOrganizationActive &&
            state.Actor is { } actor &&
            actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) &&
            _actionAuthorization.CanExecute(action, actor.Role);
    }
}
