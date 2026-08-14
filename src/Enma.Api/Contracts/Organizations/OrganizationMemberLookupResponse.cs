namespace Enma.Api.Contracts.Organizations;

public sealed record OrganizationMemberLookupItemResponse(
    Guid Id,
    string DisplayName);

public sealed record OrganizationMemberLookupResponse(
    IReadOnlyList<OrganizationMemberLookupItemResponse> Items,
    int PageNumber,
    int PageSize,
    bool HasNext);
