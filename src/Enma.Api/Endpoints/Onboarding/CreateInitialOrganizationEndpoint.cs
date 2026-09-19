using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Contracts.Onboarding;
using Enma.Application.Onboarding;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Enma.Api.Endpoints.Onboarding;

public static class CreateInitialOrganizationEndpoint
{
    private const string OrganizationSlugConflictCode =
        "organization_slug_conflict";

    public static IEndpointRouteBuilder MapCreateInitialOrganizationEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/onboarding/initial-organization",
                CreateAsync)
            .WithTags("Onboarding")
            .WithName("CreateInitialOrganization")
            .RequireAuthorization()
            .RequireEnmaAntiforgery();
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        CreateInitialOrganizationRequest request,
        HttpContext httpContext,
        CreateInitialOrganizationUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(httpContext.User, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        AuthenticateResult external = await httpContext.AuthenticateAsync(
            ExternalAuthenticationDefaults.CookieScheme);
        string? providerSubject = external.Principal?.FindFirstValue(
            ExternalAuthenticationDefaults.GoogleSubjectClaim);
        bool emailVerified = string.Equals(
            external.Principal?.FindFirstValue(
                ExternalAuthenticationDefaults.GoogleEmailVerifiedClaim),
            "true",
            StringComparison.OrdinalIgnoreCase);
        bool hasInvitationContinuation = external.Properties?.Items.ContainsKey(
            ExternalAuthenticationDefaults.InvitationItem) == true;
        if (!external.Succeeded ||
            providerSubject is null ||
            !emailVerified ||
            hasInvitationContinuation)
        {
            return TypedResults.Forbid();
        }

        InitialOrganizationResult result = await useCase.ExecuteAsync(
            userId,
            ExternalAuthenticationDefaults.GoogleProvider,
            providerSubject,
            request.OrganizationName,
            request.OrganizationSlug,
            cancellationToken);
        IResult response = result.Status switch
        {
            InitialOrganizationStatus.Succeeded => TypedResults.Created(
                $"/api/organizations/{result.OrganizationId}",
                new { organizationId = result.OrganizationId }),
            InitialOrganizationStatus.SlugConflict => TypedResults.Problem(
                new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Organization slug conflict",
                    Instance = httpContext.Request.Path,
                    Extensions =
                    {
                        ["code"] = OrganizationSlugConflictCode
                    }
                }),
            InitialOrganizationStatus.Ineligible => TypedResults.Forbid(),
            _ => TypedResults.BadRequest()
        };
        if (result.Status == InitialOrganizationStatus.Succeeded)
        {
            await httpContext.SignOutAsync(
                ExternalAuthenticationDefaults.CookieScheme);
        }

        return response;
    }
}
