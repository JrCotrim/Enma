using System.Diagnostics;
using Enma.Api.Contracts.Onboarding;
using Enma.Application.Onboarding.RegisterOrganizationOwner;
using Enma.Application.Organizations.Create;
using Enma.Application.Users;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Enma.Api.Endpoints.Onboarding;

public static class RegisterOrganizationOwnerEndpoint
{
    public static IEndpointRouteBuilder MapRegisterOrganizationOwnerEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(
                "/api/onboarding/register",
                async Task<Results<
                    CreatedAtRoute<RegisterOrganizationOwnerResponse>,
                    ProblemHttpResult>> (
                    RegisterOrganizationOwnerRequest request,
                    RegisterOrganizationOwnerHandler handler,
                    HttpContext httpContext,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        RegisterOrganizationOwnerCommand command = new(
                            request.OrganizationName,
                            request.OrganizationSlug,
                            request.OwnerName,
                            request.OwnerEmail,
                            request.Password);
                        RegisterOrganizationOwnerResult result =
                            await handler.HandleAsync(command, cancellationToken);
                        RegisterOrganizationOwnerResponse response = new(
                            result.OrganizationId,
                            result.OrganizationName,
                            result.OrganizationSlug,
                            result.UserId,
                            result.UserName,
                            result.UserEmail,
                            result.MembershipId,
                            result.Role.ToString(),
                            result.CreatedAt);

                        return TypedResults.CreatedAtRoute(
                            response,
                            "GetOrganizationById",
                            new { id = result.OrganizationId });
                    }
                    catch (OrganizationSlugAlreadyExistsException exception)
                    {
                        return CreateProblem(
                            httpContext,
                            StatusCodes.Status409Conflict,
                            "Onboarding conflict",
                            exception.Message);
                    }
                    catch (UserEmailAlreadyExistsException exception)
                    {
                        return CreateProblem(
                            httpContext,
                            StatusCodes.Status409Conflict,
                            "Onboarding conflict",
                            exception.Message);
                    }
                    catch (ArgumentException exception)
                    {
                        return CreateProblem(
                            httpContext,
                            StatusCodes.Status400BadRequest,
                            "Invalid onboarding request",
                            exception.Message);
                    }
                })
            .WithName("RegisterOrganizationOwner")
            .WithTags("Onboarding")
            .WithSummary("Registers an organization and its owner.")
            .Accepts<RegisterOrganizationOwnerRequest>("application/json")
            .Produces<RegisterOrganizationOwnerResponse>(
                StatusCodes.Status201Created,
                "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    private static ProblemHttpResult CreateProblem(
        HttpContext httpContext,
        int statusCode,
        string title,
        string detail)
    {
        ProblemDetails problemDetails = new()
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path
        };
        problemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? httpContext.TraceIdentifier;

        return TypedResults.Problem(problemDetails);
    }
}
