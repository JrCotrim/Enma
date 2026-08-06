namespace Enma.Api.Contracts.Onboarding;

public sealed record RegisterOrganizationOwnerResponse(
    Guid OrganizationId,
    string OrganizationName,
    string OrganizationSlug,
    Guid UserId,
    string UserName,
    string UserEmail,
    Guid MembershipId,
    string Role,
    DateTimeOffset CreatedAt);
