using Enma.Domain.Clients;

namespace Enma.Application.Clients;

public interface IClientMutationPersistence
{
    Task<ClientMutationPersistenceResult> UpdateNameAsync(
        ClientMutationPersistenceRequest request,
        Func<ClientMutationLockedState, ClientMutationDecision> decide,
        CancellationToken cancellationToken = default);

    Task<ClientMutationPersistenceResult> DeactivateAsync(
        ClientMutationPersistenceRequest request,
        Func<ClientMutationLockedState, ClientMutationDecision> decide,
        CancellationToken cancellationToken = default);

    Task<ClientMutationPersistenceResult> ReactivateAsync(
        ClientMutationPersistenceRequest request,
        Func<ClientMutationLockedState, ClientMutationDecision> decide,
        CancellationToken cancellationToken = default);
}

public sealed record ClientMutationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid ClientId);

public sealed record ClientMutationLockedState(
    Client Client,
    bool IsOrganizationActive,
    ClientLockedActorState? Actor);

public sealed class ClientMutationDecision
{
    private ClientMutationDecision(ClientMutationDecisionStatus status)
    {
        Status = status;
    }

    public ClientMutationDecisionStatus Status { get; }

    public static ClientMutationDecision AccessDenied { get; } = new(
        ClientMutationDecisionStatus.AccessDenied);

    public static ClientMutationDecision Persist { get; } = new(
        ClientMutationDecisionStatus.Persist);
}

public enum ClientMutationDecisionStatus
{
    AccessDenied = 0,
    Persist = 1
}

public enum ClientMutationPersistenceResult
{
    AccessDenied = 0,
    NotFound = 1,
    Succeeded = 2
}
