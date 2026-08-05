namespace Enma.Api.Contracts.Organizations;

public sealed record CreateOrganizationRequest(
    string Name,
    string Slug);
