using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Contracts.Dashboard;
using Enma.Application.Agenda;
using Enma.Application.Dashboard;

namespace Enma.Api.Endpoints.Dashboard;

public static class DashboardEndpoints
{
    private const string RoutePrefix =
        "/api/organizations/{organizationId:guid}/dashboard";

    public static IEndpointRouteBuilder MapDashboardEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup(RoutePrefix)
            .WithTags("Dashboard")
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess)
            .RequireNoStoreResponses();

        group.MapGet(string.Empty, GetAsync)
            .WithName("GetDashboard")
            .WithSummary("Gets the operational dashboard for the contextual organization.")
            .Produces<GetDashboardResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        GetDashboardUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        GetDashboardResult result = await useCase.ExecuteAsync(
            new GetDashboardQuery(userId, organizationId),
            cancellationToken);

        return result.Status switch
        {
            GetDashboardResultStatus.AccessDenied => TypedResults.Forbid(),
            GetDashboardResultStatus.Succeeded => TypedResults.Ok(
                MapResponse(result.Dashboard ?? throw new InvalidOperationException(
                    "The successful dashboard query did not return a read model."))),
            _ => throw new InvalidOperationException(
                "The dashboard query returned an unknown status.")
        };
    }

    private static GetDashboardResponse MapResponse(
        DashboardReadModel dashboard)
    {
        return new GetDashboardResponse(
            dashboard.ReferenceDate,
            new DashboardSummaryResponse(
                dashboard.Summary.ActiveClients,
                dashboard.Summary.TotalLegalProcesses,
                dashboard.Summary.PendingDeadlines,
                dashboard.Summary.PendingTasks),
            new DashboardAttentionResponse(
                MapAttentionBucket(dashboard.Attention.Deadlines),
                MapAttentionBucket(dashboard.Attention.Tasks)),
            new DashboardUpcomingResponse(
                dashboard.ThroughDate,
                dashboard.Upcoming.Deadlines
                    .Select(MapDeadline)
                    .ToArray(),
                dashboard.Upcoming.Tasks
                    .Select(MapTask)
                    .ToArray(),
                dashboard.Upcoming.CalendarEvents
                    .Select(MapCalendarEvent)
                    .ToArray()));
    }

    private static DashboardAttentionBucketResponse MapAttentionBucket(
        DashboardAttentionBucketReadModel bucket)
    {
        return new DashboardAttentionBucketResponse(
            bucket.Overdue,
            bucket.DueToday,
            bucket.DueInNextSevenDays);
    }

    private static DashboardUpcomingDeadlineResponse MapDeadline(
        UpcomingAgendaDeadlineReadModel deadline)
    {
        return new DashboardUpcomingDeadlineResponse(
            deadline.Id,
            deadline.Title,
            deadline.DueDate,
            deadline.ClientName,
            deadline.ProcessTitle);
    }

    private static DashboardUpcomingTaskResponse MapTask(
        UpcomingAgendaTaskReadModel task)
    {
        return new DashboardUpcomingTaskResponse(
            task.Id,
            task.Title,
            task.DueDate,
            task.ClientName,
            task.ProcessTitle,
            task.AssigneeDisplayName);
    }

    private static DashboardUpcomingCalendarEventResponse MapCalendarEvent(
        UpcomingAgendaCalendarEventReadModel calendarEvent)
    {
        return new DashboardUpcomingCalendarEventResponse(
            calendarEvent.Id,
            calendarEvent.Title,
            calendarEvent.StartsAt,
            calendarEvent.EndsAt,
            calendarEvent.ClientName,
            calendarEvent.ProcessTitle,
            calendarEvent.AssigneeDisplayName);
    }
}
