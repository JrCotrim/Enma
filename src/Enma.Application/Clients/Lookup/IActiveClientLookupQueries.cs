namespace Enma.Application.Clients.Lookup;

public interface IActiveClientLookupQueries
{
    Task<IReadOnlyList<ActiveClientLookupItem>> SearchAsync(
        Guid organizationId,
        string? search,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);
}
