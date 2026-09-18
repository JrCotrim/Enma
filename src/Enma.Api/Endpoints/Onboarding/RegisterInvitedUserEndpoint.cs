using System.Diagnostics;
using Enma.Api.Contracts.Onboarding;
using Enma.Api.Endpoints.Organizations;
using Enma.Application.Onboarding.RegisterInvitedUser;
using Enma.Application.Security;
using Enma.Application.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Enma.Api.Endpoints.Onboarding;

public static class RegisterInvitedUserEndpoint
{
    private const string InvalidInvitationCode =
        "invited_registration_invalid";
    private const string WrongRecipientCode =
        "invited_registration_wrong_recipient";

    public static IEndpointRouteBuilder MapRegisterInvitedUserEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(
                "/api/onboarding/register-invited",
                async Task<Results<
                    Created<RegisterInvitedUserResponse>,
                    ProblemHttpResult>> (
                    RegisterInvitedUserRequest request,
                    RegisterInvitedUserHandler handler,
                    HttpContext httpContext,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        RegisterInvitedUserResult result =
                            await handler.HandleAsync(
                                new RegisterInvitedUserCommand(
                                    request.InvitationToken,
                                    request.Name,
                                    request.Email,
                                    request.Password),
                                cancellationToken);

                        return result.Status switch
                        {
                            RegisterInvitedUserStatus.InvalidInvitation =>
                                CreateProblem(
                                    httpContext,
                                    StatusCodes.Status400BadRequest,
                                    "Invalid invited registration",
                                    "The invited registration request is invalid.",
                                    InvalidInvitationCode),
                            RegisterInvitedUserStatus.WrongRecipient =>
                                CreateProblem(
                                    httpContext,
                                    StatusCodes.Status400BadRequest,
                                    "Wrong invitation recipient",
                                    "Use the email address that received this invitation.",
                                    WrongRecipientCode),
                            RegisterInvitedUserStatus.ExistingUser =>
                                CreateProblem(
                                    httpContext,
                                    StatusCodes.Status409Conflict,
                                    "Invited registration conflict",
                                    "An account already exists for this invitation."),
                            RegisterInvitedUserStatus.Succeeded =>
                                TypedResults.Created(
                                    "/login",
                                    new RegisterInvitedUserResponse(
                                        result.VerificationEmailSent)),
                            _ => throw new InvalidOperationException(
                                "Invited registration returned an unknown status.")
                        };
                    }
                    catch (CompromisedPasswordException exception)
                    {
                        return CreateProblem(
                            httpContext,
                            StatusCodes.Status400BadRequest,
                            "Invalid invited registration",
                            exception.Message);
                    }
                    catch (CompromisedPasswordCheckUnavailableException exception)
                    {
                        return CreateProblem(
                            httpContext,
                            StatusCodes.Status503ServiceUnavailable,
                            "Password screening unavailable",
                            exception.Message);
                    }
                    catch (RequestValidationException exception)
                    {
                        return CreateProblem(
                            httpContext,
                            StatusCodes.Status400BadRequest,
                            "Invalid invited registration",
                            exception.Message);
                    }
                })
            .WithName("RegisterInvitedUser")
            .WithTags("Onboarding")
            .WithSummary("Registers an account for an organization invitation.")
            .Accepts<RegisterInvitedUserRequest>("application/json")
            .Produces<RegisterInvitedUserResponse>(
                StatusCodes.Status201Created,
                "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireRateLimiting(
                OrganizationInvitationEndpoints.RecipientTokenRateLimitPolicy);

        return endpoints;
    }

    private static ProblemHttpResult CreateProblem(
        HttpContext httpContext,
        int statusCode,
        string title,
        string detail,
        string? code = null)
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

        if (code is not null)
        {
            problemDetails.Extensions["code"] = code;
        }

        return TypedResults.Problem(problemDetails);
    }
}
