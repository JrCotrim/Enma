using Enma.Api.Contracts.Organizations;
using Enma.Application.Organizations.Create;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

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

        group.MapPost(
                "",
                async Task<Created<CreateOrganizationResponse>> (
                    CreateOrganizationRequest request,
                    CreateOrganizationHandler handler,
                    CancellationToken cancellationToken) =>
                {
                    CreateOrganizationCommand command = new(
                        request.Name,
                        request.Slug);
                    CreateOrganizationResult result = await handler.HandleAsync(
                        command,
                        cancellationToken);
                    CreateOrganizationResponse response = new(
                        result.Id,
                        result.Name,
                        result.Slug,
                        result.IsActive,
                        result.CreatedAt);

                    return TypedResults.Created(
                        $"/api/organizations/{response.Id}",
                        response);
                })
            .WithName("CreateOrganization")
            .WithSummary("Creates an organization.")
            .Accepts<CreateOrganizationRequest>("application/json")
            .Produces<CreateOrganizationResponse>(
                StatusCodes.Status201Created,
                "application/json")
            .Produces<ProblemDetails>(
                StatusCodes.Status400BadRequest,
                "application/problem+json")
            .Produces<ProblemDetails>(
                StatusCodes.Status409Conflict,
                "application/problem+json")
            .Produces<ProblemDetails>(
                StatusCodes.Status500InternalServerError,
                "application/problem+json");

        return endpoints;
    }
}
