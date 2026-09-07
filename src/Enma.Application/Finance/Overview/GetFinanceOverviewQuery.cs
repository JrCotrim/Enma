namespace Enma.Application.Finance.Overview;

public sealed record GetFinanceOverviewQuery(
    Guid UserId,
    Guid OrganizationId);
