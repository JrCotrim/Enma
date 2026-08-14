namespace Enma.Application.Organizations.Members.Lookup;

public interface IOrganizationMemberLookupQueries
{
    Task<IReadOnlyList<OrganizationMemberLookupItem>> SearchAsync(
        Guid organizationId,
        string? search,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);
}
