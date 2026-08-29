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
        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                userId,
                organizationId,
                ClientAction.Create,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return CreateClientResult.AccessDenied;
        }

        var request = new ClientCreationPersistenceRequest(
            userId,
            organizationId,
            actorMembershipId);
        ClientCreationPersistenceResult persistenceResult =
            await _creationPersistence.ExecuteAsync(
                request,
                state => DecideCreation(request, state, name),
                cancellationToken);

        return persistenceResult.Status switch
        {
            ClientCreationDecisionStatus.AccessDenied =>
                CreateClientResult.AccessDenied,
            ClientCreationDecisionStatus.Persist
                when persistenceResult.ClientId is Guid clientId =>
                CreateClientResult.Success(clientId),
            _ => throw new InvalidOperationException(
                "Client creation persistence returned an invalid result.")
        };
    }

    private ClientCreationDecision DecideCreation(
        ClientCreationPersistenceRequest request,
        ClientCreationLockedState state,
        string name)
    {
        if (!state.IsOrganizationActive ||
            state.Actor is not { } actor ||
            !actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) ||
            !_actionAuthorization.CanExecute(ClientAction.Create, actor.Role))
        {
            return ClientCreationDecision.AccessDenied;
        }

        return ClientCreationDecision.Persist(
            CreateClient(
                request.OrganizationId,
                name,
                _timeProvider.GetUtcNow()));
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
