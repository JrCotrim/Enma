using System.Globalization;
using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Contracts.Organizations;
using Enma.Application.Organizations.Invitations;
using Enma.Domain.Organizations;
using Microsoft.AspNetCore.RateLimiting;

namespace Enma.Api.Endpoints.Organizations;

public static class OrganizationInvitationEndpoints
{
    public const string SendRateLimitPolicy = "organization-invitation-send";

    private const string RoutePrefix =
        "/api/organizations/{organizationId:guid}/invitations";

    public static IEndpointRouteBuilder MapOrganizationInvitationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup(RoutePrefix)
            .WithTags("Organization Invitations")
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess)
            .RequireNoStoreResponses();

        group.MapPost("/", CreateAsync)
            .WithName("CreateOrganizationInvitation")
            .WithSummary("Creates and sends an organization invitation.")
            .Accepts<CreateOrganizationInvitationRequest>("application/json")
            .Produces<OrganizationInvitationMutationResponse>(
                StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireRateLimiting(SendRateLimitPolicy)
            .RequireEnmaAntiforgery();

        group.MapGet("/", ListAsync)
            .WithName("ListOrganizationInvitations")
            .WithSummary("Lists organization invitations for administrators.")
            .Produces<ListOrganizationInvitationsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPost("{invitationId:guid}/revoke", RevokeAsync)
            .WithName("RevokeOrganizationInvitation")
            .WithSummary("Revokes a pending organization invitation.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        group.MapPost("{invitationId:guid}/resend", ResendAsync)
            .WithName("ResendOrganizationInvitation")
            .WithSummary("Rotates and sends a pending organization invitation.")
            .Produces<OrganizationInvitationMutationResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireRateLimiting(SendRateLimitPolicy)
            .RequireEnmaAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        Guid organizationId,
        CreateOrganizationInvitationRequest request,
        ClaimsPrincipal principal,
        CreateOrganizationInvitationUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        CreateOrganizationInvitationResult result = await useCase.ExecuteAsync(
            new CreateOrganizationInvitationCommand(
                userId,
                organizationId,
                request.Email,
                request.Role),
            cancellationToken);

        return result.Status switch
        {
            CreateOrganizationInvitationStatus.AccessDenied =>
                TypedResults.Forbid(),
            CreateOrganizationInvitationStatus.ExistingActiveMembership =>
                CreateConflict(
                    "The invitee is already an active organization member."),
            CreateOrganizationInvitationStatus
                .IncompatibleInactiveMembership =>
                CreateConflict(
                    "The inactive membership has a different role."),
            CreateOrganizationInvitationStatus.DuplicatePendingInvitation =>
                CreateConflict(
                    "A pending invitation already exists for this email."),
            CreateOrganizationInvitationStatus.Succeeded =>
                TypedResults.Created(
                    $"/api/organizations/{organizationId}/invitations",
                    new OrganizationInvitationMutationResponse(
                        result.InvitationId
                            ?? throw new InvalidOperationException(
                                "Successful invitation creation must include an id."),
                        MapDeliveryStatus(result.DeliveryStatus))),
            _ => throw new InvalidOperationException(
                "Invitation creation returned an unknown status.")
        };
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        ListOrganizationInvitationsUseCase useCase,
        CancellationToken cancellationToken,
        int pageNumber = 1,
        int pageSize = ListOrganizationInvitationsUseCase.DefaultPageSize)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        ListOrganizationInvitationsResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            pageNumber,
            pageSize,
            cancellationToken);

        if (result.Status == ListOrganizationInvitationsStatus.AccessDenied)
        {
            return TypedResults.Forbid();
        }

        OrganizationInvitationResponse[] items = result.Items
            .Select(invitation => new OrganizationInvitationResponse(
                invitation.Id,
                invitation.InvitedEmail,
                MapRole(invitation.Role),
                invitation.Status.ToString(),
                invitation.CreatedAt,
                invitation.ExpiresAt,
                invitation.CreatedByMembershipId))
            .ToArray();

        return TypedResults.Ok(new ListOrganizationInvitationsResponse(
            items,
            result.PageNumber,
            result.PageSize,
            result.TotalCount));
    }

    private static Task<IResult> RevokeAsync(
        Guid organizationId,
        Guid invitationId,
        ClaimsPrincipal principal,
        OrganizationInvitationLifecycleUseCase useCase,
        CancellationToken cancellationToken)
    {
        return ExecuteLifecycleAsync(
            organizationId,
            invitationId,
            principal,
            useCase.RevokeAsync,
            resend: false,
            httpContext: null,
            cancellationToken);
    }

    private static Task<IResult> ResendAsync(
        Guid organizationId,
        Guid invitationId,
        ClaimsPrincipal principal,
        OrganizationInvitationLifecycleUseCase useCase,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        return ExecuteLifecycleAsync(
            organizationId,
            invitationId,
            principal,
            useCase.ResendAsync,
            resend: true,
            httpContext,
            cancellationToken);
    }

    private static async Task<IResult> ExecuteLifecycleAsync(
        Guid organizationId,
        Guid invitationId,
        ClaimsPrincipal principal,
        Func<Guid, Guid, Guid, CancellationToken,
            Task<OrganizationInvitationLifecycleResult>> execute,
        bool resend,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        OrganizationInvitationLifecycleResult result = await execute(
            userId,
            organizationId,
            invitationId,
            cancellationToken);

        if (result.Status == OrganizationInvitationLifecycleStatus.Cooldown)
        {
            TimeSpan retryAfter = result.RetryAfter ?? TimeSpan.FromSeconds(1);
            httpContext!.Response.Headers.RetryAfter = Math.Max(
                    1,
                    (int)Math.Ceiling(retryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
        }

        return result.Status switch
        {
            OrganizationInvitationLifecycleStatus.AccessDenied =>
                TypedResults.Forbid(),
            OrganizationInvitationLifecycleStatus.NotFound =>
                TypedResults.NotFound(),
            OrganizationInvitationLifecycleStatus.Conflict =>
                CreateConflict(
                    "The invitation cannot be changed in its current state."),
            OrganizationInvitationLifecycleStatus.Cooldown =>
                TypedResults.Problem(
                    title: "Invitation resend limited",
                    detail: "Wait before resending this invitation.",
                    statusCode: StatusCodes.Status429TooManyRequests),
            OrganizationInvitationLifecycleStatus.Succeeded when !resend =>
                TypedResults.NoContent(),
            OrganizationInvitationLifecycleStatus.Succeeded =>
                TypedResults.Ok(new OrganizationInvitationMutationResponse(
                    invitationId,
                    MapDeliveryStatus(result.DeliveryStatus))),
            _ => throw new InvalidOperationException(
                "Invitation lifecycle mutation returned an unknown status.")
        };
    }

    private static string MapDeliveryStatus(
        OrganizationInvitationDeliveryResult? result)
    {
        return result switch
        {
            OrganizationInvitationDeliveryResult.Accepted => "accepted",
            OrganizationInvitationDeliveryResult.Failed => "failed",
            _ => throw new InvalidOperationException(
                "Successful invitation delivery must include a status.")
        };
    }

    private static string MapRole(OrganizationRole role)
    {
        return role switch
        {
            OrganizationRole.Administrator => "Administrator",
            OrganizationRole.Member => "Member",
            _ => throw new InvalidOperationException(
                "An invitation has an unsupported role.")
        };
    }

    private static IResult CreateConflict(string detail)
    {
        return TypedResults.Problem(
            title: "Resource conflict",
            detail: detail,
            statusCode: StatusCodes.Status409Conflict);
    }
}
