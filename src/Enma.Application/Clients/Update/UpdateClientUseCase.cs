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

    public Task<UpdateClientResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        Guid clientId,
        string name,
        CancellationToken cancellationToken = default)
    {
        return ExecuteCoreAsync(
            userId,
            organizationId,
            clientId,
            state => state.Client.ChangeName(name),
            cancellationToken);
    }

    public Task<UpdateClientResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        Guid clientId,
        string name,
        string? email,
        string? phone,
        string? cpf,
        CancellationToken cancellationToken = default)
    {
        return ExecuteCoreAsync(
            userId,
            organizationId,
            clientId,
            state => state.Client.UpdateProfile(
                name,
                email,
                phone,
                cpf),
            cancellationToken);
    }

    private async Task<UpdateClientResult> ExecuteCoreAsync(
        Guid userId,
        Guid organizationId,
        Guid clientId,
        Action<ClientMutationLockedState> mutate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutate);

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
                state => DecideUpdate(request, state, mutate),
                cancellationToken);
        }
        catch (ArgumentException exception)
            when (IsProfileParameter(exception.ParamName))
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
        Action<ClientMutationLockedState> mutate)
    {
        if (!IsAuthorized(request, state, ClientAction.Update))
        {
            return ClientMutationDecision.AccessDenied;
        }

        mutate(state);
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

    private static bool IsProfileParameter(string? parameterName)
    {
        return parameterName is "name" or "email" or "phone" or "cpf";
    }
}