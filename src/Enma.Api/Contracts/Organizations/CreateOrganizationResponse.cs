namespace Enma.Api.Contracts.Organizations;

public sealed record CreateOrganizationResponse(
    Guid Id,
    string Name,
    string Slug,
    bool IsActive,
    DateTimeOffset CreatedAt);
