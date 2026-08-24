namespace Enma.Application.Dashboard;

public sealed record GetDashboardQuery(
    Guid UserId,
    Guid OrganizationId);
