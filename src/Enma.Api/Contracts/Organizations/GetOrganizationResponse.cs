namespace Enma.Api.Contracts.Organizations;

public sealed record GetOrganizationResponse(
    Guid Id,
    string Name,
    string Slug,
    bool IsActive,
    DateTimeOffset CreatedAt);
