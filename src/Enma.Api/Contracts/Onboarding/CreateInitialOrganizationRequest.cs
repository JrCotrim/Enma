namespace Enma.Api.Contracts.Onboarding;

public sealed class CreateInitialOrganizationRequest
{
    public string OrganizationName { get; init; } = string.Empty;

    public string OrganizationSlug { get; init; } = string.Empty;
}
