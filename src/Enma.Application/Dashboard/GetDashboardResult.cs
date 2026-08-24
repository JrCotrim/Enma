namespace Enma.Application.Dashboard;

public sealed class GetDashboardResult
{
    private GetDashboardResult(
        GetDashboardResultStatus status,
        DashboardReadModel? dashboard)
    {
        Status = status;
        Dashboard = dashboard;
    }

    public GetDashboardResultStatus Status { get; }

    public DashboardReadModel? Dashboard { get; }

    public static GetDashboardResult AccessDenied { get; } = new(
        GetDashboardResultStatus.AccessDenied,
        null);

    public static GetDashboardResult Succeeded(DashboardReadModel dashboard)
    {
        ArgumentNullException.ThrowIfNull(dashboard);

        return new GetDashboardResult(
            GetDashboardResultStatus.Succeeded,
            dashboard);
    }
}

public enum GetDashboardResultStatus
{
    AccessDenied = 0,
    Succeeded = 1
}
