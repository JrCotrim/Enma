namespace Enma.Application.Organizations.Create;

public sealed record CreateOrganizationCommand(
    string Name,
    string Slug);
