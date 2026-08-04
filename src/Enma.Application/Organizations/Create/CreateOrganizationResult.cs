namespace Enma.Application.Organizations.Create;

public sealed record CreateOrganizationResult(
    Guid Id,
    string Name,
    string Slug,
    bool IsActive,
    DateTimeOffset CreatedAt);
