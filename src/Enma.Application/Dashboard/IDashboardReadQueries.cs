namespace Enma.Application.Dashboard;

public interface IDashboardReadQueries
{
    Task<DashboardMetricsReadModel> ReadMetricsAsync(
        DashboardMetricsReadRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record DashboardMetricsReadRequest(
    Guid OrganizationId,
    DateOnly ReferenceDate,
    DateOnly ThroughDate);
