using Enma.Application.Agenda;
using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.Application.Dashboard;

public sealed class GetDashboardUseCase
{
    private readonly OrganizationAccessAuthorization _accessAuthorization;
    private readonly IDashboardReadQueries _dashboardReadQueries;
    private readonly IAgendaReadQueries _agendaReadQueries;
    private readonly TimeProvider _timeProvider;

    public GetDashboardUseCase(
        OrganizationAccessAuthorization accessAuthorization,
        IDashboardReadQueries dashboardReadQueries,
        IAgendaReadQueries agendaReadQueries,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(accessAuthorization);
        ArgumentNullException.ThrowIfNull(dashboardReadQueries);
        ArgumentNullException.ThrowIfNull(agendaReadQueries);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _accessAuthorization = accessAuthorization;
        _dashboardReadQueries = dashboardReadQueries;
        _agendaReadQueries = agendaReadQueries;
        _timeProvider = timeProvider;
    }

    public async Task<GetDashboardResult> ExecuteAsync(
        GetDashboardQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!await HasViewAccessAsync(query, cancellationToken))
        {
            return GetDashboardResult.AccessDenied;
        }

        DateTimeOffset nowUtc = _timeProvider
            .GetUtcNow()
            .ToUniversalTime();
        DateOnly referenceDate = DateOnly.FromDateTime(nowUtc.UtcDateTime);
        DateOnly throughDate = referenceDate.AddDays(7);
        var eventWindowEndUtc = new DateTimeOffset(
            throughDate.AddDays(1),
            TimeOnly.MinValue,
            TimeSpan.Zero);

        DashboardMetricsReadModel metrics =
            await _dashboardReadQueries.ReadMetricsAsync(
                new DashboardMetricsReadRequest(
                    query.OrganizationId,
                    referenceDate,
                    throughDate),
                cancellationToken);
        UpcomingAgendaReadModel upcoming =
            await _agendaReadQueries.ReadUpcomingAsync(
                new UpcomingAgendaReadRequest(
                    query.OrganizationId,
                    referenceDate,
                    throughDate,
                    nowUtc,
                    eventWindowEndUtc),
                cancellationToken);

        return GetDashboardResult.Succeeded(
            new DashboardReadModel(
                referenceDate,
                throughDate,
                new DashboardSummaryReadModel(
                    metrics.ActiveClients,
                    metrics.TotalLegalProcesses,
                    metrics.PendingDeadlines,
                    metrics.PendingTasks),
                new DashboardAttentionReadModel(
                    new DashboardAttentionBucketReadModel(
                        metrics.OverdueDeadlines,
                        metrics.DeadlinesDueToday,
                        metrics.DeadlinesDueInNextSevenDays),
                    new DashboardAttentionBucketReadModel(
                        metrics.OverdueTasks,
                        metrics.TasksDueToday,
                        metrics.TasksDueInNextSevenDays)),
                upcoming));
    }

    private async Task<bool> HasViewAccessAsync(
        GetDashboardQuery query,
        CancellationToken cancellationToken)
    {
        OrganizationAccessAuthorizationResult access;

        try
        {
            access = await _accessAuthorization.AuthorizeAsync(
                query.UserId,
                query.OrganizationId,
                cancellationToken);
        }
        catch (ArgumentOutOfRangeException exception) when (
            exception.ParamName == "role")
        {
            return false;
        }

        return access.Status == OrganizationAccessAuthorizationStatus.Allowed &&
            access.UserId == query.UserId &&
            access.OrganizationId == query.OrganizationId &&
            access.MembershipId is Guid &&
            access.Role is OrganizationRole.Owner or
                OrganizationRole.Administrator or
                OrganizationRole.Member;
    }
}
