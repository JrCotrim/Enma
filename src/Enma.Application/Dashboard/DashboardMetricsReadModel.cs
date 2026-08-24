namespace Enma.Application.Dashboard;

public sealed record DashboardMetricsReadModel(
    int ActiveClients,
    int TotalLegalProcesses,
    int PendingDeadlines,
    int PendingTasks,
    int OverdueDeadlines,
    int DeadlinesDueToday,
    int DeadlinesDueInNextSevenDays,
    int OverdueTasks,
    int TasksDueToday,
    int TasksDueInNextSevenDays);
