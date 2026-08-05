using Enma.Domain.Organizations;

namespace Enma.Application.Onboarding.RegisterOrganizationOwner;

public sealed record RegisterOrganizationOwnerResult(
    Guid OrganizationId,
    string OrganizationName,
    string OrganizationSlug,
    Guid UserId,
    string UserName,
    string UserEmail,
    Guid MembershipId,
    OrganizationRole Role,
    DateTimeOffset CreatedAt);
