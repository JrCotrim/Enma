using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Contracts.Auditing;
using Enma.Api.Endpoints;
using Enma.Application.Auditing.List;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;

namespace Enma.Api.Endpoints.Auditing;

public static class AuditLogEndpoints
{
    private const string RoutePrefix =
        "/api/organizations/{organizationId:guid}/audit-logs";

    public static IEndpointRouteBuilder MapAuditLogEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGroup(RoutePrefix)
            .WithTags("Audit Logs")
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess)
            .RequireNoStoreResponses()
            .MapGet(string.Empty, ListAsync)
            .WithName("ListAuditLogs")
            .WithSummary("Lists administrative audit events for the organization.")
            .Produces<ListAuditLogsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        ListAuditLogsUseCase useCase,
        CancellationToken cancellationToken,
        string? eventType = null,
        string? entityType = null,
        Guid? entityId = null,
        int pageNumber = 1,
        int pageSize = ListAuditLogsUseCase.DefaultPageSize)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        ListAuditLogsResult result = await useCase.ExecuteAsync(
            new ListAuditLogsQuery(
                userId,
                organizationId,
                eventType,
                entityType,
                entityId,
                pageNumber,
                pageSize),
            cancellationToken);

        if (result.Status == ListAuditLogsResultStatus.AccessDenied)
        {
            return TypedResults.Forbid();
        }

        return TypedResults.Ok(new ListAuditLogsResponse(
            result.Items.Select(MapItem).ToArray(),
            result.PageNumber,
            result.PageSize,
            result.TotalCount));
    }

    private static AuditLogResponse MapItem(AuditLogReadModel item)
    {
        return new AuditLogResponse(
            item.Id,
            item.ActorMembershipId,
            MapRole(item.ActorRoleAtOccurrence),
            item.EventType.ToCode(),
            item.EntityType.ToCode(),
            item.EntityId,
            item.OccurredAt,
            MapDetails(item.Details));
    }

    private static AuditLogDetailsResponse? MapDetails(AuditEventDetails? details)
    {
        return details switch
        {
            null => null,
            OrganizationRenamedAuditDetails value =>
                new OrganizationRenamedAuditLogDetailsResponse(
                    value.OldName,
                    value.NewName),
            OrganizationMembershipRoleChangedAuditDetails value =>
                new OrganizationMembershipRoleChangedAuditLogDetailsResponse(
                    MapRole(value.OldRole),
                    MapRole(value.NewRole)),
            OrganizationInvitationCreatedAuditDetails value =>
                new OrganizationInvitationCreatedAuditLogDetailsResponse(
                    MapRole(value.Role)),
            LegalDeadlineDetailsChangedAuditDetails value =>
                new LegalDeadlineDetailsChangedAuditLogDetailsResponse(
                    value.ChangedFields.Select(MapChangedField).ToArray()),
            LegalTaskDetailsChangedAuditDetails value =>
                new LegalTaskDetailsChangedAuditLogDetailsResponse(
                    value.ChangedFields.Select(MapChangedField).ToArray()),
            LegalTaskAssigneeChangedAuditDetails value =>
                new LegalTaskAssigneeChangedAuditLogDetailsResponse(
                    value.OldAssigneeMembershipId,
                    value.NewAssigneeMembershipId),
            CalendarEventUpdatedAuditDetails value =>
                new CalendarEventUpdatedAuditLogDetailsResponse(
                    value.ChangedFields.Select(MapChangedField).ToArray()),
            CalendarEventAssigneeChangedAuditDetails value =>
                new CalendarEventAssigneeChangedAuditLogDetailsResponse(
                    value.OldAssigneeMembershipId,
                    value.NewAssigneeMembershipId),
            _ => throw new InvalidOperationException(
                "The audit log contains unsupported details.")
        };
    }

    private static string MapRole(OrganizationRole role)
    {
        return role switch
        {
            OrganizationRole.Owner => "Owner",
            OrganizationRole.Administrator => "Administrator",
            OrganizationRole.Member => "Member",
            _ => throw new InvalidOperationException(
                "The audit log contains an unsupported organization role.")
        };
    }

    private static string MapChangedField(LegalDeadlineChangedField field)
    {
        return field switch
        {
            LegalDeadlineChangedField.Title => "Title",
            LegalDeadlineChangedField.DueDate => "DueDate",
            _ => throw new InvalidOperationException(
                "The audit log contains an unsupported deadline field.")
        };
    }

    private static string MapChangedField(LegalTaskChangedField field)
    {
        return field switch
        {
            LegalTaskChangedField.Title => "Title",
            LegalTaskChangedField.Description => "Description",
            LegalTaskChangedField.DueDate => "DueDate",
            LegalTaskChangedField.ProcessId => "ProcessId",
            _ => throw new InvalidOperationException(
                "The audit log contains an unsupported task field.")
        };
    }

    private static string MapChangedField(CalendarEventChangedField field)
    {
        return field switch
        {
            CalendarEventChangedField.Title => "Title",
            CalendarEventChangedField.Description => "Description",
            CalendarEventChangedField.StartsAt => "StartsAt",
            CalendarEventChangedField.EndsAt => "EndsAt",
            CalendarEventChangedField.Location => "Location",
            CalendarEventChangedField.ClientId => "ClientId",
            CalendarEventChangedField.ProcessId => "ProcessId",
            _ => throw new InvalidOperationException(
                "The audit log contains an unsupported calendar field.")
        };
    }
}
