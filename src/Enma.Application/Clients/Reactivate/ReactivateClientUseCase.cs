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
        ClientActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                ClientAction.Reactivate,
                cancellationToken);

        if (authorization == ClientActionAuthorizationResult.Denied)
        {
            return ReactivateClientResult.AccessDenied;
        }

        if (clientId == Guid.Empty)
        {
            return ReactivateClientResult.NotFound;
        }

        ClientMutationPersistenceResult persistenceResult =
            await _mutationPersistence.ReactivateAsync(
                clientId,
                organizationId,
                cancellationToken);

        return persistenceResult == ClientMutationPersistenceResult.Succeeded
            ? ReactivateClientResult.Succeeded
            : ReactivateClientResult.NotFound;
    }
}
