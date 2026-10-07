namespace Enma.Application.Organizations.Members.Ownership;

public sealed record TransferOrganizationOwnershipCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid MembershipId,
    string? ExpectedTargetRole);
