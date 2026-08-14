using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Contracts.Organizations;
using Enma.Api.Endpoints;
using Enma.Application.Organizations.Members.Lookup;

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

        group.MapGet("lookup", LookupAsync)
            .WithName("LookupActiveOrganizationMembers")
            .WithSummary("Finds active members in the contextual organization.")
            .Produces<OrganizationMemberLookupResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return endpoints;
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
}
