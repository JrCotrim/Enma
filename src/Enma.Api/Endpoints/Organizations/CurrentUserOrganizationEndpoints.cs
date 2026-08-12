using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Contracts.Organizations;
using Enma.Application.Organizations.CurrentUser;
using Enma.Domain.Organizations;

namespace Enma.Api.Endpoints.Organizations;

public static class CurrentUserOrganizationEndpoints
{
    public static IEndpointRouteBuilder MapCurrentUserOrganizationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/me/organizations", GetAsync)
            .WithTags("Organizations")
            .WithName("GetCurrentUserOrganizations")
            .WithSummary(
                "Lists the active organizations accessible to the current user.")
            .WithDescription(
                "Returns a live navigation and UX snapshot. Resource operations " +
                "remain subject to live server-side authorization.")
            .Produces<GetCurrentUserOrganizationsResponse>(
                StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal principal,
        HttpResponse response,
        GetCurrentUserOrganizationsUseCase useCase,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";

        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        IReadOnlyList<CurrentUserOrganizationReadModel> organizations =
            await useCase.ExecuteAsync(userId, cancellationToken);
        CurrentUserOrganizationResponse[] items = organizations
            .Select(MapOrganization)
            .ToArray();

        return TypedResults.Ok(new GetCurrentUserOrganizationsResponse(items));
    }

    private static CurrentUserOrganizationResponse MapOrganization(
        CurrentUserOrganizationReadModel organization)
    {
        return new CurrentUserOrganizationResponse(
            organization.OrganizationId,
            organization.Name,
            MapRole(organization.Role));
    }

    private static string MapRole(OrganizationRole role)
    {
        return role switch
        {
            OrganizationRole.Owner => "Owner",
            OrganizationRole.Administrator => "Administrator",
            OrganizationRole.Member => "Member",
            _ => throw new InvalidOperationException(
                "The organization query returned an unknown role.")
        };
    }
}
