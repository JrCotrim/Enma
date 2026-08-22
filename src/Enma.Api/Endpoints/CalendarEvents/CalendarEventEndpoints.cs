using System.Globalization;
using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Contracts.CalendarEvents;
using Enma.Application.CalendarEvents;
using Enma.Application.CalendarEvents.Assignment;
using Enma.Application.CalendarEvents.Create;
using Enma.Application.CalendarEvents.Delete;
using Enma.Application.CalendarEvents.GetById;
using Enma.Application.CalendarEvents.Update;

namespace Enma.Api.Endpoints.CalendarEvents;

public static class CalendarEventEndpoints
{
    private const string RoutePrefix =
        "/api/organizations/{organizationId:guid}/calendar-events";

    public static IEndpointRouteBuilder MapCalendarEventEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup(RoutePrefix)
            .WithTags("Calendar Events")
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess)
            .RequireNoStoreResponses();

        group.MapPost(string.Empty, CreateAsync)
            .WithName("CreateCalendarEvent")
            .WithSummary("Creates a calendar event in the contextual organization.")
            .Accepts<CreateCalendarEventRequest>("application/json")
            .Produces<CreateCalendarEventResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        group.MapGet("{calendarEventId:guid}", GetAsync)
            .WithName("GetCalendarEvent")
            .WithSummary("Gets a calendar event in the contextual organization.")
            .Produces<CalendarEventResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPut("{calendarEventId:guid}", UpdateAsync)
            .WithName("UpdateCalendarEvent")
            .WithSummary("Updates a calendar event in the contextual organization.")
            .Accepts<UpdateCalendarEventRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        group.MapPut("{calendarEventId:guid}/assignee", ChangeAssigneeAsync)
            .WithName("ChangeCalendarEventAssignee")
            .WithSummary("Changes a calendar event assignee in the contextual organization.")
            .Accepts<ChangeCalendarEventAssigneeRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        group.MapDelete("{calendarEventId:guid}", DeleteAsync)
            .WithName("DeleteCalendarEvent")
            .WithSummary("Deletes a calendar event in the contextual organization.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        Guid organizationId,
        CreateCalendarEventRequest request,
        ClaimsPrincipal principal,
        CreateCalendarEventUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        CreateCalendarEventResult result = await useCase.ExecuteAsync(
            new CreateCalendarEventCommand(
                userId,
                organizationId,
                request.Title,
                request.Description,
                request.StartsAt,
                request.EndsAt,
                request.Location,
                request.ClientId,
                request.ProcessId,
                request.AssigneeMembershipId),
            cancellationToken);

        if (result.Status == CreateCalendarEventResultStatus.Created)
        {
            Guid calendarEventId = result.CalendarEventId
                ?? throw new InvalidOperationException(
                    "A successful calendar event creation did not provide an id.");
            string location = string.Create(
                CultureInfo.InvariantCulture,
                $"/api/organizations/{organizationId:D}/calendar-events/{calendarEventId:D}");

            return TypedResults.Created(
                location,
                new CreateCalendarEventResponse(calendarEventId));
        }

        return result.Status switch
        {
            CreateCalendarEventResultStatus.AccessDenied => TypedResults.Forbid(),
            CreateCalendarEventResultStatus.RelatedClientUnavailable =>
                TypedResults.NotFound(),
            CreateCalendarEventResultStatus.RelatedProcessUnavailable =>
                TypedResults.NotFound(),
            CreateCalendarEventResultStatus.RelatedAssigneeUnavailable =>
                CreateRelatedAssigneeUnavailableProblem(),
            CreateCalendarEventResultStatus.InvalidInput => TypedResults.BadRequest(),
            _ => throw new InvalidOperationException(
                "The calendar event creation returned an unknown status.")
        };
    }

    private static async Task<IResult> GetAsync(
        Guid organizationId,
        Guid calendarEventId,
        ClaimsPrincipal principal,
        GetCalendarEventUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        GetCalendarEventResult result = await useCase.ExecuteAsync(
            new GetCalendarEventQuery(
                userId,
                organizationId,
                calendarEventId),
            cancellationToken);

        return result.Status switch
        {
            GetCalendarEventResultStatus.AccessDenied => TypedResults.Forbid(),
            GetCalendarEventResultStatus.NotFound => TypedResults.NotFound(),
            GetCalendarEventResultStatus.InvalidInput => TypedResults.BadRequest(),
            GetCalendarEventResultStatus.Succeeded => TypedResults.Ok(
                MapDetail(
                    result.CalendarEvent ?? throw new InvalidOperationException(
                        "A successful calendar event query did not provide an event."))),
            _ => throw new InvalidOperationException(
                "The calendar event query returned an unknown status.")
        };
    }

    private static async Task<IResult> UpdateAsync(
        Guid organizationId,
        Guid calendarEventId,
        UpdateCalendarEventRequest request,
        ClaimsPrincipal principal,
        UpdateCalendarEventUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        UpdateCalendarEventResult result = await useCase.ExecuteAsync(
            new UpdateCalendarEventCommand(
                userId,
                organizationId,
                calendarEventId,
                request.Title,
                request.Description,
                request.StartsAt,
                request.EndsAt,
                request.Location,
                request.ClientId,
                request.ProcessId),
            cancellationToken);

        return result switch
        {
            UpdateCalendarEventResult.AccessDenied => TypedResults.Forbid(),
            UpdateCalendarEventResult.NotFound => TypedResults.NotFound(),
            UpdateCalendarEventResult.RelatedClientUnavailable =>
                TypedResults.NotFound(),
            UpdateCalendarEventResult.RelatedProcessUnavailable =>
                TypedResults.NotFound(),
            UpdateCalendarEventResult.InvalidInput => TypedResults.BadRequest(),
            UpdateCalendarEventResult.Succeeded => TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The calendar event update returned an unknown status.")
        };
    }

    private static async Task<IResult> ChangeAssigneeAsync(
        Guid organizationId,
        Guid calendarEventId,
        ChangeCalendarEventAssigneeRequest request,
        ClaimsPrincipal principal,
        ChangeCalendarEventAssigneeUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        ChangeCalendarEventAssigneeResult result = await useCase.ExecuteAsync(
            new ChangeCalendarEventAssigneeCommand(
                userId,
                organizationId,
                calendarEventId,
                request.AssigneeMembershipId),
            cancellationToken);

        return result switch
        {
            ChangeCalendarEventAssigneeResult.AccessDenied => TypedResults.Forbid(),
            ChangeCalendarEventAssigneeResult.NotFound => TypedResults.NotFound(),
            ChangeCalendarEventAssigneeResult.RelatedAssigneeUnavailable =>
                CreateRelatedAssigneeUnavailableProblem(),
            ChangeCalendarEventAssigneeResult.InvalidInput =>
                TypedResults.BadRequest(),
            ChangeCalendarEventAssigneeResult.Succeeded => TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The calendar event assignment returned an unknown status.")
        };
    }

    private static async Task<IResult> DeleteAsync(
        Guid organizationId,
        Guid calendarEventId,
        ClaimsPrincipal principal,
        DeleteCalendarEventUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        DeleteCalendarEventResult result = await useCase.ExecuteAsync(
            new DeleteCalendarEventCommand(
                userId,
                organizationId,
                calendarEventId),
            cancellationToken);

        return result switch
        {
            DeleteCalendarEventResult.AccessDenied => TypedResults.Forbid(),
            DeleteCalendarEventResult.NotFound => TypedResults.NotFound(),
            DeleteCalendarEventResult.Deleted => TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The calendar event deletion returned an unknown status.")
        };
    }

    private static CalendarEventResponse MapDetail(
        CalendarEventDetailReadModel calendarEvent)
    {
        return new CalendarEventResponse(
            calendarEvent.Id,
            calendarEvent.Title,
            calendarEvent.Description,
            calendarEvent.StartsAt,
            calendarEvent.EndsAt,
            calendarEvent.Location,
            calendarEvent.ClientId,
            calendarEvent.ClientName,
            calendarEvent.ProcessId,
            calendarEvent.ProcessTitle,
            calendarEvent.AssigneeMembershipId,
            calendarEvent.AssigneeDisplayName,
            calendarEvent.CreatedByMembershipId,
            calendarEvent.CreatedByDisplayName,
            calendarEvent.CreatedAt);
    }

    private static IResult CreateRelatedAssigneeUnavailableProblem()
    {
        return TypedResults.Problem(
            title: "Related assignee unavailable",
            detail: "The requested assignee is unavailable.",
            statusCode: StatusCodes.Status400BadRequest);
    }
}
