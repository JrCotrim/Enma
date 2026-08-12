using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Contracts.Organizations;
using Enma.Application.Organizations.GetById;

namespace Enma.Api.Endpoints.Organizations;

public static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/organizations")
            .WithTags("Organizations");

        group.MapGet("{organizationId:guid}", GetAsync)
            .WithName("GetOrganizationById")
            .WithSummary("Gets an organization by its identifier.")
            .Produces<GetOrganizationResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        HttpResponse response,
        GetOrganizationByIdHandler handler,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";

        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        GetOrganizationByIdResult result = await handler.HandleAsync(
            userId,
            organizationId,
            cancellationToken);

        return result.Status switch
        {
            GetOrganizationByIdResultStatus.AccessDenied => TypedResults.Forbid(),
            GetOrganizationByIdResultStatus.NotFound => TypedResults.NotFound(),
            GetOrganizationByIdResultStatus.Succeeded => TypedResults.Ok(
                MapOrganization(result.Organization ??
                    throw new InvalidOperationException(
                        "A successful organization query did not provide metadata."))),
            _ => throw new InvalidOperationException(
                "The organization query returned an unknown status.")
        };
    }

    private static GetOrganizationResponse MapOrganization(
        OrganizationMetadataReadModel organization)
    {
        return new GetOrganizationResponse(
            organization.Id,
            organization.Name,
            organization.Slug,
            organization.IsActive,
            organization.CreatedAt);
    }
}
