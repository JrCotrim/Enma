namespace Enma.Application.Clients;

public interface IClientMutationPersistence
{
    Task<ClientMutationPersistenceResult> UpdateNameAsync(
        Guid clientId,
        Guid organizationId,
        string name,
        CancellationToken cancellationToken = default);

    Task<ClientMutationPersistenceResult> DeactivateAsync(
        Guid clientId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<ClientMutationPersistenceResult> ReactivateAsync(
        Guid clientId,
        Guid organizationId,
        CancellationToken cancellationToken = default);
}

public enum ClientMutationPersistenceResult
{
    NotFound = 0,
    Succeeded = 1
}
