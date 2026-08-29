using Enma.Application.Authorization;

namespace Enma.Application.Clients.Deactivate;

public sealed class DeactivateClientUseCase
{
    private readonly ClientActionAuthorization _actionAuthorization;
    private readonly IClientMutationPersistence _mutationPersistence;

    public DeactivateClientUseCase(
        ClientActionAuthorization actionAuthorization,
        IClientMutationPersistence mutationPersistence)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);

        _actionAuthorization = actionAuthorization;
        _mutationPersistence = mutationPersistence;
    }

    public async Task<DeactivateClientResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                userId,
                organizationId,
                ClientAction.Deactivate,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return DeactivateClientResult.AccessDenied;
        }

        if (clientId == Guid.Empty)
        {
            return DeactivateClientResult.NotFound;
        }

        var request = new ClientMutationPersistenceRequest(
            userId,
            organizationId,
            actorMembershipId,
            clientId);
        ClientMutationPersistenceResult persistenceResult =
            await _mutationPersistence.DeactivateAsync(
                request,
                state => DecideDeactivation(request, state),
                cancellationToken);

        return persistenceResult switch
        {
            ClientMutationPersistenceResult.AccessDenied =>
                DeactivateClientResult.AccessDenied,
            ClientMutationPersistenceResult.NotFound =>
                DeactivateClientResult.NotFound,
            ClientMutationPersistenceResult.Succeeded =>
                DeactivateClientResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Client mutation persistence returned an invalid result.")
        };
    }

    private ClientMutationDecision DecideDeactivation(
        ClientMutationPersistenceRequest request,
        ClientMutationLockedState state)
    {
        if (!state.IsOrganizationActive ||
            state.Actor is not { } actor ||
            !actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) ||
            !_actionAuthorization.CanExecute(
                ClientAction.Deactivate,
                actor.Role))
        {
            return ClientMutationDecision.AccessDenied;
        }

        state.Client.Deactivate();
        return ClientMutationDecision.Persist;
    }
}
