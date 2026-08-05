namespace Enma.Application.Onboarding.RegisterOrganizationOwner;

public sealed record RegisterOrganizationOwnerCommand(
    string OrganizationName,
    string OrganizationSlug,
    string OwnerName,
    string OwnerEmail);
