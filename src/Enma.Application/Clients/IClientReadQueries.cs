namespace Enma.Application.Clients;

public interface IClientReadQueries
{
    Task<ClientReadModel?> FindAsync(
        Guid clientId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClientReadModel>> ListAsync(
        Guid organizationId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);
}
