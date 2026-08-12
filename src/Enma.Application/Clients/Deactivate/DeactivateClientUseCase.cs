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
        ClientActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                ClientAction.Deactivate,
                cancellationToken);

        if (authorization == ClientActionAuthorizationResult.Denied)
        {
            return DeactivateClientResult.AccessDenied;
        }

        if (clientId == Guid.Empty)
        {
            return DeactivateClientResult.NotFound;
        }

        ClientMutationPersistenceResult persistenceResult =
            await _mutationPersistence.DeactivateAsync(
                clientId,
                organizationId,
                cancellationToken);

        return persistenceResult == ClientMutationPersistenceResult.Succeeded
            ? DeactivateClientResult.Succeeded
            : DeactivateClientResult.NotFound;
    }
}
