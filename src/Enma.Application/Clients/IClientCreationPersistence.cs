using Enma.Domain.Clients;
using Enma.Domain.Organizations;

namespace Enma.Application.Clients;

public interface IClientCreationPersistence
{
    Task<ClientCreationPersistenceResult> ExecuteAsync(
        ClientCreationPersistenceRequest request,
        Func<ClientCreationLockedState, ClientCreationDecision> decide,
        CancellationToken cancellationToken = default);
}

public sealed record ClientCreationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId);

public sealed record ClientCreationLockedState(
    bool IsOrganizationActive,
    ClientLockedActorState? Actor);

public sealed record ClientLockedActorState(
    Guid MembershipId,
    Guid OrganizationId,
    Guid UserId,
    OrganizationRole Role,
    bool IsMembershipActive,
    bool IsUserActive)
{
    public bool IsAvailableFor(
        Guid userId,
        Guid organizationId,
        Guid membershipId)
    {
        return MembershipId == membershipId &&
            OrganizationId == organizationId &&
            UserId == userId &&
            IsMembershipActive &&
            IsUserActive &&
            Enum.IsDefined(Role);
    }
}

public sealed class ClientCreationDecision
{
    private ClientCreationDecision(
        ClientCreationDecisionStatus status,
        Client? client)
    {
        Status = status;
        Client = client;
    }

    public ClientCreationDecisionStatus Status { get; }

    public Client? Client { get; }

    public static ClientCreationDecision AccessDenied { get; } = new(
        ClientCreationDecisionStatus.AccessDenied,
        null);

    public static ClientCreationDecision Persist(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);

        return new ClientCreationDecision(
            ClientCreationDecisionStatus.Persist,
            client);
    }
}

public enum ClientCreationDecisionStatus
{
    AccessDenied = 0,
    Persist = 1
}

public sealed class ClientCreationPersistenceResult
{
    private ClientCreationPersistenceResult(
        ClientCreationDecisionStatus status,
        Guid? clientId)
    {
        Status = status;
        ClientId = clientId;
    }

    public ClientCreationDecisionStatus Status { get; }

    public Guid? ClientId { get; }

    public static ClientCreationPersistenceResult AccessDenied { get; } = new(
        ClientCreationDecisionStatus.AccessDenied,
        null);

    public static ClientCreationPersistenceResult Created(Guid clientId)
    {
        if (clientId == Guid.Empty)
        {
            throw new ArgumentException(
                "Client id cannot be empty.",
                nameof(clientId));
        }

        return new ClientCreationPersistenceResult(
            ClientCreationDecisionStatus.Persist,
            clientId);
    }
}
