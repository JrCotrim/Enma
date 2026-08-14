namespace Enma.Application.Processes.Lookup;

public interface ILegalProcessLookupQueries
{
    Task<IReadOnlyList<LegalProcessLookupItem>> SearchAsync(
        Guid organizationId,
        string? search,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);
}
