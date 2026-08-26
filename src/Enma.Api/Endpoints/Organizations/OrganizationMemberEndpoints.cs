using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Contracts.Organizations;
using Enma.Api.Endpoints;
using Enma.Application.Organizations.Members.List;
using Enma.Application.Organizations.Members.Lifecycle;
using Enma.Application.Organizations.Members.Lookup;
using Enma.Application.Organizations.Members.Role;
using Enma.Domain.Organizations;

namespace Enma.Api.Endpoints.Organizations;

public static class OrganizationMemberEndpoints
{
    private const string RoutePrefix =
        "/api/organizations/{organizationId:guid}/members";

    public static IEndpointRouteBuilder MapOrganizationMemberEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup(RoutePrefix)
            .WithTags("Organization Members")
            .RequireAuthorization()
            .RequireNoStoreResponses();

        group.MapGet("/", ListAsync)
            .WithName("ListOrganizationMembers")
            .WithSummary("Lists organization members visible to the current actor.")
            .Produces<ListOrganizationMembersResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess);

        group.MapGet("lookup", LookupAsync)
            .WithName("LookupActiveOrganizationMembers")
            .WithSummary("Finds active members in the contextual organization.")
            .Produces<OrganizationMemberLookupResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPut("{membershipId:guid}/role", ChangeRoleAsync)
            .WithName("ChangeOrganizationMemberRole")
            .WithSummary("Changes an active organization membership role.")
            .Accepts<ChangeOrganizationMemberRoleRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess)
            .RequireEnmaAntiforgery();

        group.MapPost("{membershipId:guid}/deactivate", DeactivateAsync)
            .WithName("DeactivateOrganizationMember")
            .WithSummary("Deactivates an organization membership.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess)
            .RequireEnmaAntiforgery();

        group.MapPost("{membershipId:guid}/reactivate", ReactivateAsync)
            .WithName("ReactivateOrganizationMember")
            .WithSummary("Reactivates an organization membership.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess)
            .RequireEnmaAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        ListOrganizationMembersUseCase useCase,
        CancellationToken cancellationToken,
        string? status = null,
        string? search = null,
        int pageNumber = 1,
        int pageSize = ListOrganizationMembersUseCase.DefaultPageSize)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        ListOrganizationMembersResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            status,
            search,
            pageNumber,
            pageSize,
            cancellationToken);

        if (result.Status == ListOrganizationMembersResultStatus.AccessDenied)
        {
            return TypedResults.Forbid();
        }

        OrganizationMemberResponse[] items = result.Items
            .Select(MapMember)
            .ToArray();

        return TypedResults.Ok(new ListOrganizationMembersResponse(
            items,
            result.PageNumber,
            result.PageSize,
            result.TotalCount));
    }

    private static async Task<IResult> LookupAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        SearchActiveOrganizationMembersUseCase useCase,
        CancellationToken cancellationToken,
        string? search = null,
        int pageNumber = 1,
        int pageSize = SearchActiveOrganizationMembersUseCase.DefaultPageSize)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        SearchActiveOrganizationMembersResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            search,
            pageNumber,
            pageSize,
            cancellationToken);

        if (result.Status ==
            SearchActiveOrganizationMembersResultStatus.AccessDenied)
        {
            return TypedResults.Forbid();
        }

        OrganizationMemberLookupItemResponse[] items = result.Items
            .Select(member => new OrganizationMemberLookupItemResponse(
                member.Id,
                member.DisplayName))
            .ToArray();

        return TypedResults.Ok(new OrganizationMemberLookupResponse(
            items,
            result.PageNumber,
            result.PageSize,
            result.HasNext));
    }

    private static async Task<IResult> ChangeRoleAsync(
        Guid organizationId,
        Guid membershipId,
        ChangeOrganizationMemberRoleRequest request,
        ClaimsPrincipal principal,
        ChangeOrganizationMemberRoleUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        ChangeOrganizationMemberRoleResult result = await useCase.ExecuteAsync(
            new ChangeOrganizationMemberRoleCommand(
                userId,
                organizationId,
                membershipId,
                request.Role,
                request.ExpectedCurrentRole),
            cancellationToken);

        return result switch
        {
            ChangeOrganizationMemberRoleResult.AccessDenied =>
                TypedResults.Forbid(),
            ChangeOrganizationMemberRoleResult.NotFound =>
                TypedResults.NotFound(),
            ChangeOrganizationMemberRoleResult.TargetForbidden =>
                TypedResults.Forbid(),
            ChangeOrganizationMemberRoleResult.Conflict =>
                CreateRoleConflictProblem(),
            ChangeOrganizationMemberRoleResult.Succeeded =>
                TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The organization member role change returned an unknown status.")
        };
    }

    private static Task<IResult> DeactivateAsync(
        Guid organizationId,
        Guid membershipId,
        ClaimsPrincipal principal,
        OrganizationMemberLifecycleUseCase useCase,
        CancellationToken cancellationToken)
    {
        return ChangeLifecycleAsync(
            organizationId,
            membershipId,
            principal,
            useCase.DeactivateAsync,
            cancellationToken);
    }

    private static Task<IResult> ReactivateAsync(
        Guid organizationId,
        Guid membershipId,
        ClaimsPrincipal principal,
        OrganizationMemberLifecycleUseCase useCase,
        CancellationToken cancellationToken)
    {
        return ChangeLifecycleAsync(
            organizationId,
            membershipId,
            principal,
            useCase.ReactivateAsync,
            cancellationToken);
    }

    private static async Task<IResult> ChangeLifecycleAsync(
        Guid organizationId,
        Guid membershipId,
        ClaimsPrincipal principal,
        Func<Guid, Guid, Guid, CancellationToken,
            Task<OrganizationMemberLifecycleResult>> execute,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        OrganizationMemberLifecycleResult result = await execute(
            userId,
            organizationId,
            membershipId,
            cancellationToken);

        return result switch
        {
            OrganizationMemberLifecycleResult.AccessDenied =>
                TypedResults.Forbid(),
            OrganizationMemberLifecycleResult.NotFound =>
                TypedResults.NotFound(),
            OrganizationMemberLifecycleResult.ActiveAssignmentsConflict =>
                CreateLifecycleConflictProblem(
                    "The member has active assigned work and cannot be deactivated."),
            OrganizationMemberLifecycleResult.InactiveUserConflict =>
                CreateLifecycleConflictProblem(
                    "The member account is inactive and cannot be reactivated."),
            OrganizationMemberLifecycleResult.Succeeded =>
                TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The organization member lifecycle change returned an unknown status.")
        };
    }

    private static OrganizationMemberResponse MapMember(
        OrganizationMemberAdministrationReadModel member)
    {
        return new OrganizationMemberResponse(
            member.Id,
            member.Name,
            MapRole(member.Role),
            member.Email,
            member.MembershipStatus?.ToString(),
            member.AccountStatus?.ToString());
    }

    private static string MapRole(OrganizationRole role)
    {
        return role switch
        {
            OrganizationRole.Owner => "Owner",
            OrganizationRole.Administrator => "Administrator",
            OrganizationRole.Member => "Member",
            _ => throw new InvalidOperationException(
                "An organization member has an unsupported role.")
        };
    }

    private static IResult CreateRoleConflictProblem()
    {
        return TypedResults.Problem(
            title: "Resource conflict",
            detail: "The membership role cannot be changed in its current state.",
            statusCode: StatusCodes.Status409Conflict);
    }

    private static IResult CreateLifecycleConflictProblem(string detail)
    {
        return TypedResults.Problem(
            title: "Resource conflict",
            detail: detail,
            statusCode: StatusCodes.Status409Conflict);
    }
}
