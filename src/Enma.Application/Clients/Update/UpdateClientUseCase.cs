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
        ClientActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                ClientAction.Update,
                cancellationToken);

        if (authorization == ClientActionAuthorizationResult.Denied)
        {
            return UpdateClientResult.AccessDenied;
        }

        if (clientId == Guid.Empty)
        {
            return UpdateClientResult.NotFound;
        }

        ClientMutationPersistenceResult persistenceResult;

        try
        {
            persistenceResult = await _mutationPersistence.UpdateNameAsync(
                clientId,
                organizationId,
                name,
                cancellationToken);
        }
        catch (ArgumentException exception) when (exception.ParamName == "name")
        {
            throw new RequestValidationException(exception.Message, exception);
        }

        return persistenceResult == ClientMutationPersistenceResult.Succeeded
            ? UpdateClientResult.Succeeded
            : UpdateClientResult.NotFound;
    }
}
