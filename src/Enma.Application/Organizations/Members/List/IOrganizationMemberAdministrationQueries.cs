namespace Enma.Application.Organizations.Members.List;

public interface IOrganizationMemberAdministrationQueries
{
    Task<OrganizationMemberAdministrationPage> ListAsync(
        OrganizationMemberAdministrationQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record OrganizationMemberAdministrationQuery(
    Guid OrganizationId,
    OrganizationMembershipStatus MembershipStatus,
    string? Search,
    int PageNumber,
    int PageSize,
    OrganizationMemberDetailLevel DetailLevel);

public sealed record OrganizationMemberAdministrationPage(
    IReadOnlyList<OrganizationMemberAdministrationReadModel> Items,
    int TotalCount);

public enum OrganizationMemberDetailLevel
{
    Basic = 1,
    Administrative = 2
}
