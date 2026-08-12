using Enma.Domain.Organizations;

namespace Enma.Application.Organizations.CurrentUser;

public sealed record CurrentUserOrganizationReadModel(
    Guid OrganizationId,
    string Name,
    OrganizationRole Role);
