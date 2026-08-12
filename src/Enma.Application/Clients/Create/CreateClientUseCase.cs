using Enma.Application.Authorization;
using Enma.Application.Validation;
using Enma.Domain.Clients;

namespace Enma.Application.Clients.Create;

public sealed class CreateClientUseCase
{
    private readonly ClientActionAuthorization _actionAuthorization;
    private readonly IClientCreationPersistence _creationPersistence;
    private readonly TimeProvider _timeProvider;

    public CreateClientUseCase(
        ClientActionAuthorization actionAuthorization,
        IClientCreationPersistence creationPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(creationPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _actionAuthorization = actionAuthorization;
        _creationPersistence = creationPersistence;
        _timeProvider = timeProvider;
    }

    public async Task<CreateClientResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ClientActionAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                ClientAction.Create,
                cancellationToken);

        if (authorization == ClientActionAuthorizationResult.Denied)
        {
            return CreateClientResult.AccessDenied;
        }

        Client client = CreateClient(
            organizationId,
            name,
            _timeProvider.GetUtcNow());

        await _creationPersistence.PersistAsync(client, cancellationToken);

        return CreateClientResult.Success(client.Id);
    }

    private static Client CreateClient(
        Guid organizationId,
        string name,
        DateTimeOffset createdAt)
    {
        try
        {
            return new Client(organizationId, name, createdAt);
        }
        catch (ArgumentException exception) when (exception.ParamName == "name")
        {
            throw new RequestValidationException(exception.Message, exception);
        }
    }
}
