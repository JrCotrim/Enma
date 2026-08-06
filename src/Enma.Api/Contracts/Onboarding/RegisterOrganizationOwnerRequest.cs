namespace Enma.Api.Contracts.Onboarding;

public sealed class RegisterOrganizationOwnerRequest
{
    public required string OrganizationName { get; init; }

    public required string OrganizationSlug { get; init; }

    public required string OwnerName { get; init; }

    public required string OwnerEmail { get; init; }

    public required string Password { get; init; }
}
