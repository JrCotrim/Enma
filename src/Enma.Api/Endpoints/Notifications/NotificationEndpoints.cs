using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Contracts.Notifications;
using Enma.Application.Notifications;
using Enma.Application.Notifications.List;
using Enma.Application.Notifications.MarkAllRead;
using Enma.Application.Notifications.MarkRead;
using Enma.Domain.Notifications;

namespace Enma.Api.Endpoints.Notifications;

public static class NotificationEndpoints
{
    private const string RoutePrefix =
        "/api/organizations/{organizationId:guid}/notifications";

    public static IEndpointRouteBuilder MapNotificationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup(RoutePrefix)
            .WithTags("Notifications")
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess)
            .RequireNoStoreResponses();

        group.MapGet(string.Empty, ListAsync)
            .WithName("ListNotifications")
            .WithSummary("Lists the current user's recent notifications.")
            .Produces<ListNotificationsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPut("read-all", MarkAllAsReadAsync)
            .WithName("MarkAllNotificationsAsRead")
            .WithSummary("Marks all of the current user's notifications as read.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        group.MapPut("{notificationId:guid}/read", MarkAsReadAsync)
            .WithName("MarkNotificationAsRead")
            .WithSummary("Marks one of the current user's notifications as read.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        ListNotificationsUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        ListNotificationsResult result = await useCase.ExecuteAsync(
            new ListNotificationsQuery(userId, organizationId),
            cancellationToken);

        return result.Status switch
        {
            ListNotificationsResultStatus.AccessDenied => TypedResults.Forbid(),
            ListNotificationsResultStatus.Succeeded => TypedResults.Ok(
                new ListNotificationsResponse(
                    result.Items.Select(MapItem).ToArray(),
                    result.UnreadCount)),
            _ => throw new InvalidOperationException(
                "The notification list returned an unknown status.")
        };
    }

    private static async Task<IResult> MarkAsReadAsync(
        Guid organizationId,
        Guid notificationId,
        ClaimsPrincipal principal,
        MarkNotificationAsReadUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        MarkNotificationAsReadResult result = await useCase.ExecuteAsync(
            new MarkNotificationAsReadCommand(
                userId,
                organizationId,
                notificationId),
            cancellationToken);

        return result switch
        {
            MarkNotificationAsReadResult.AccessDenied => TypedResults.Forbid(),
            MarkNotificationAsReadResult.NotFound => TypedResults.NotFound(),
            MarkNotificationAsReadResult.Succeeded => TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The notification mutation returned an unknown status.")
        };
    }

    private static async Task<IResult> MarkAllAsReadAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        MarkAllNotificationsAsReadUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        MarkAllNotificationsAsReadResult result = await useCase.ExecuteAsync(
            new MarkAllNotificationsAsReadCommand(userId, organizationId),
            cancellationToken);

        return result switch
        {
            MarkAllNotificationsAsReadResult.AccessDenied => TypedResults.Forbid(),
            MarkAllNotificationsAsReadResult.Succeeded => TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The notification bulk mutation returned an unknown status.")
        };
    }

    private static NotificationResponse MapItem(NotificationReadModel item)
    {
        return new NotificationResponse(
            item.Id,
            MapKind(item.Kind),
            MapSourceType(item.Kind),
            item.SourceId,
            item.SourceTitle,
            item.OccurrenceDate,
            item.OccurrenceAt,
            item.GeneratedAt,
            item.ReadAt);
    }

    private static NotificationKindResponse MapKind(NotificationKind kind)
    {
        return kind switch
        {
            NotificationKind.LegalDeadlineDueSoon =>
                NotificationKindResponse.LegalDeadlineDueSoon,
            NotificationKind.LegalTaskDueSoon =>
                NotificationKindResponse.LegalTaskDueSoon,
            NotificationKind.CalendarEventStartingSoon =>
                NotificationKindResponse.CalendarEventStartingSoon,
            _ => throw new InvalidOperationException(
                "The notification read model returned an unknown kind.")
        };
    }

    private static NotificationSourceTypeResponse MapSourceType(
        NotificationKind kind)
    {
        return kind switch
        {
            NotificationKind.LegalDeadlineDueSoon =>
                NotificationSourceTypeResponse.LegalDeadline,
            NotificationKind.LegalTaskDueSoon =>
                NotificationSourceTypeResponse.LegalTask,
            NotificationKind.CalendarEventStartingSoon =>
                NotificationSourceTypeResponse.CalendarEvent,
            _ => throw new InvalidOperationException(
                "The notification read model returned an unknown kind.")
        };
    }
}
