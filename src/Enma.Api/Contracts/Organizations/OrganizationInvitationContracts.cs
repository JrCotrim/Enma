namespace Enma.Api.Contracts.Organizations;

public sealed class CreateOrganizationInvitationRequest
{
    public required string Email { get; init; }

    public required string Role { get; init; }
}

public sealed class OrganizationInvitationTokenRequest
{
    public required string Token { get; init; }
}

public sealed record OrganizationInvitationPreviewResponse(
    string Status,
    string? OrganizationName = null,
    string? Role = null,
    string? InvitedEmail = null);

public sealed record OrganizationInvitationMutationResponse(
    Guid InvitationId,
    string DeliveryStatus);

public sealed record ListOrganizationInvitationsResponse(
    IReadOnlyList<OrganizationInvitationResponse> Items,
    int PageNumber,
    int PageSize,
    int TotalCount);

public sealed record OrganizationInvitationResponse(
    Guid Id,
    string InvitedEmail,
    string Role,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    Guid CreatedByMembershipId);
