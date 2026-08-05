using Enma.Api.Contracts.Organizations;
using Enma.Application.Organizations.Create;
using Enma.Application.Organizations.GetById;
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
                async Task<CreatedAtRoute<CreateOrganizationResponse>> (
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

                    return TypedResults.CreatedAtRoute(
                        response,
                        "GetOrganizationById",
                        new { id = response.Id });
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

        group.MapGet(
                "{id:guid}",
                async Task<Ok<GetOrganizationResponse>> (
                    Guid id,
                    GetOrganizationByIdHandler handler,
                    CancellationToken cancellationToken) =>
                {
                    GetOrganizationByIdResult result = await handler.HandleAsync(
                        id,
                        cancellationToken);
                    GetOrganizationResponse response = new(
                        result.Id,
                        result.Name,
                        result.Slug,
                        result.IsActive,
                        result.CreatedAt);

                    return TypedResults.Ok(response);
                })
            .WithName("GetOrganizationById")
            .WithSummary("Gets an organization by its identifier.")
            .Produces<GetOrganizationResponse>(
                StatusCodes.Status200OK,
                "application/json")
            .Produces<ProblemDetails>(
                StatusCodes.Status400BadRequest,
                "application/problem+json")
            .Produces<ProblemDetails>(
                StatusCodes.Status404NotFound,
                "application/problem+json")
            .Produces<ProblemDetails>(
                StatusCodes.Status500InternalServerError,
                "application/problem+json");

        return endpoints;
    }
}
