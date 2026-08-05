namespace Enma.Application.Organizations.GetById;

public sealed record GetOrganizationByIdResult(
    Guid Id,
    string Name,
    string Slug,
    bool IsActive,
    DateTimeOffset CreatedAt);
