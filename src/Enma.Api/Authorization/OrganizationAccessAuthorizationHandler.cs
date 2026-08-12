using Enma.Api.Authentication;
using Enma.Application.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Enma.Api.Authorization;

public sealed class OrganizationAccessAuthorizationHandler(
    OrganizationAccessAuthorization organizationAccessAuthorization)
    : AuthorizationHandler<OrganizationAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OrganizationAccessRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true ||
            context.Resource is not HttpContext httpContext ||
            !AuthenticatedUserId.TryGet(context.User, out Guid userId) ||
            !TryGetOrganizationId(httpContext, out Guid organizationId))
        {
            return;
        }

        OrganizationAccessAuthorizationResult result =
            await organizationAccessAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                httpContext.RequestAborted);

        if (result.Status == OrganizationAccessAuthorizationStatus.Allowed)
        {
            context.Succeed(requirement);
        }
    }

    private static bool TryGetOrganizationId(
        HttpContext httpContext,
        out Guid organizationId)
    {
        object? routeValue = httpContext.Request.RouteValues[
            EnmaAuthorizationPolicies.OrganizationIdRouteValue];

        return Guid.TryParse(routeValue?.ToString(), out organizationId) &&
            organizationId != Guid.Empty;
    }
}
