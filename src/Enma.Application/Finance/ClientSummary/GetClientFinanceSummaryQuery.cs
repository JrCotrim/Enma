namespace Enma.Application.Finance.ClientSummary;

public sealed record GetClientFinanceSummaryQuery(
    Guid UserId,
    Guid OrganizationId,
    Guid ClientId);
