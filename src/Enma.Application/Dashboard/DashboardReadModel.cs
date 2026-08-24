using Enma.Application.Agenda;

namespace Enma.Application.Dashboard;

public sealed record DashboardReadModel(
    DateOnly ReferenceDate,
    DateOnly ThroughDate,
    DashboardSummaryReadModel Summary,
    DashboardAttentionReadModel Attention,
    UpcomingAgendaReadModel Upcoming);

public sealed record DashboardSummaryReadModel(
    int ActiveClients,
    int TotalLegalProcesses,
    int PendingDeadlines,
    int PendingTasks);

public sealed record DashboardAttentionReadModel(
    DashboardAttentionBucketReadModel Deadlines,
    DashboardAttentionBucketReadModel Tasks);

public sealed record DashboardAttentionBucketReadModel(
    int Overdue,
    int DueToday,
    int DueInNextSevenDays);
