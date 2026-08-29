using Enma.Application.Authorization;

namespace Enma.Application.Clients.Reactivate;

public sealed class ReactivateClientUseCase
{
    private readonly ClientActionAuthorization _actionAuthorization;
    private readonly IClientMutationPersistence _mutationPersistence;

    public ReactivateClientUseCase(
        ClientActionAuthorization actionAuthorization,
        IClientMutationPersistence mutationPersistence)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);

        _actionAuthorization = actionAuthorization;
        _mutationPersistence = mutationPersistence;
    }

    public async Task<ReactivateClientResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                userId,
                organizationId,
                ClientAction.Reactivate,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return ReactivateClientResult.AccessDenied;
        }

        if (clientId == Guid.Empty)
        {
            return ReactivateClientResult.NotFound;
        }

        var request = new ClientMutationPersistenceRequest(
            userId,
            organizationId,
            actorMembershipId,
            clientId);
        ClientMutationPersistenceResult persistenceResult =
            await _mutationPersistence.ReactivateAsync(
                request,
                state => DecideReactivation(request, state),
                cancellationToken);

        return persistenceResult switch
        {
            ClientMutationPersistenceResult.AccessDenied =>
                ReactivateClientResult.AccessDenied,
            ClientMutationPersistenceResult.NotFound =>
                ReactivateClientResult.NotFound,
            ClientMutationPersistenceResult.Succeeded =>
                ReactivateClientResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Client mutation persistence returned an invalid result.")
        };
    }

    private ClientMutationDecision DecideReactivation(
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
                ClientAction.Reactivate,
                actor.Role))
        {
            return ClientMutationDecision.AccessDenied;
        }

        state.Client.Activate();
        return ClientMutationDecision.Persist;
    }
}
