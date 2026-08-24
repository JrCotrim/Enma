namespace Enma.Api.Contracts.Dashboard;

public sealed record GetDashboardResponse(
    DateOnly ReferenceDate,
    DashboardSummaryResponse Summary,
    DashboardAttentionResponse Attention,
    DashboardUpcomingResponse Upcoming);

public sealed record DashboardSummaryResponse(
    int ActiveClients,
    int TotalLegalProcesses,
    int PendingDeadlines,
    int PendingTasks);

public sealed record DashboardAttentionResponse(
    DashboardAttentionBucketResponse Deadlines,
    DashboardAttentionBucketResponse Tasks);

public sealed record DashboardAttentionBucketResponse(
    int Overdue,
    int DueToday,
    int DueInNextSevenDays);

public sealed record DashboardUpcomingResponse(
    DateOnly ThroughDate,
    IReadOnlyList<DashboardUpcomingDeadlineResponse> Deadlines,
    IReadOnlyList<DashboardUpcomingTaskResponse> Tasks,
    IReadOnlyList<DashboardUpcomingCalendarEventResponse> CalendarEvents);

public sealed record DashboardUpcomingDeadlineResponse(
    Guid Id,
    string Title,
    DateOnly DueDate,
    string ClientName,
    string ProcessTitle);

public sealed record DashboardUpcomingTaskResponse(
    Guid Id,
    string Title,
    DateOnly DueDate,
    string? ClientName,
    string? ProcessTitle,
    string? AssigneeDisplayName);

public sealed record DashboardUpcomingCalendarEventResponse(
    Guid Id,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? ClientName,
    string? ProcessTitle,
    string? AssigneeDisplayName);
