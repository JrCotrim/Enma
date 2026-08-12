namespace Enma.Api.Contracts.Organizations;

public sealed record GetCurrentUserOrganizationsResponse(
    IReadOnlyList<CurrentUserOrganizationResponse> Items);

public sealed record CurrentUserOrganizationResponse(
    Guid Id,
    string Name,
    string Role);
