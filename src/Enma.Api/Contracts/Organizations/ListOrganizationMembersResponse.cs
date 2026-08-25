using System.Text.Json.Serialization;

namespace Enma.Api.Contracts.Organizations;

public sealed record ListOrganizationMembersResponse(
    IReadOnlyList<OrganizationMemberResponse> Items,
    int PageNumber,
    int PageSize,
    int TotalCount);

public sealed record OrganizationMemberResponse(
    Guid Id,
    string Name,
    string Role,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Email,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? MembershipStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AccountStatus);
