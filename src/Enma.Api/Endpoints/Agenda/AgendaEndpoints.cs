using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Contracts.Agenda;
using Enma.Application.Agenda;

namespace Enma.Api.Endpoints.Agenda;

public static class AgendaEndpoints
{
    private const string RoutePrefix =
        "/api/organizations/{organizationId:guid}/agenda";

    public static IEndpointRouteBuilder MapAgendaEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup(RoutePrefix)
            .WithTags("Agenda")
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess)
            .RequireNoStoreResponses();

        group.MapGet(string.Empty, GetAsync)
            .WithName("GetAgenda")
            .WithSummary("Gets the unified agenda viewport for the contextual organization.")
            .Produces<GetAgendaResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        Guid organizationId,
        DateTimeOffset from,
        DateTimeOffset to,
        ClaimsPrincipal principal,
        GetAgendaUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        GetAgendaResult result = await useCase.ExecuteAsync(
            new GetAgendaQuery(userId, organizationId, from, to),
            cancellationToken);

        return result.Status switch
        {
            GetAgendaResultStatus.AccessDenied => TypedResults.Forbid(),
            GetAgendaResultStatus.InvalidInput => TypedResults.BadRequest(),
            GetAgendaResultStatus.Succeeded => TypedResults.Ok(
                new GetAgendaResponse(
                    result.Items.Select(MapItem).ToArray())),
            _ => throw new InvalidOperationException(
                "The agenda query returned an unknown status.")
        };
    }

    private static AgendaItemResponse MapItem(AgendaItemReadModel item)
    {
        return new AgendaItemResponse(
            MapKind(item.Kind),
            item.Id,
            item.Title,
            item.IsAllDay,
            item.Date,
            item.StartsAt,
            item.EndsAt,
            item.CompletedAt,
            item.ClientId,
            item.ClientName,
            item.ProcessId,
            item.ProcessTitle,
            item.AssigneeMembershipId,
            item.AssigneeDisplayName);
    }

    private static AgendaItemKindResponse MapKind(AgendaItemKind kind)
    {
        return kind switch
        {
            AgendaItemKind.Deadline => AgendaItemKindResponse.Deadline,
            AgendaItemKind.Task => AgendaItemKindResponse.Task,
            AgendaItemKind.CalendarEvent => AgendaItemKindResponse.CalendarEvent,
            _ => throw new InvalidOperationException(
                "The agenda query returned an unknown item kind.")
        };
    }
}
