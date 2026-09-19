using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Contracts.Authentication;
using Enma.Application.Authentication;
using Enma.Application.Organizations.Invitations;
using Microsoft.AspNetCore.Authentication;

namespace Enma.Api.Endpoints.Authentication;

public static class GoogleAuthenticationEndpoints
{
    internal const string RateLimitPolicy = "GoogleAuthentication";
    private const string CompletePath = "/api/auth/google/complete";

    public static IEndpointRouteBuilder MapGoogleAuthenticationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/auth/providers",
            (GoogleAuthenticationAvailability availability) =>
                TypedResults.Ok(new { google = availability.Enabled }))
            .WithTags("Authentication")
            .WithName("GetAuthenticationProviders");

        endpoints.MapPost("/api/auth/google/start", StartAsync)
            .WithTags("Authentication")
            .WithName("StartGoogleAuthentication")
            .RequireRateLimiting(RateLimitPolicy);

        endpoints.MapGet(CompletePath, CompleteAsync)
            .WithTags("Authentication")
            .WithName("CompleteGoogleAuthentication")
            .RequireRateLimiting(RateLimitPolicy);

        endpoints.MapPost(
                "/api/auth/google/complete-profile",
                CompleteProfileAsync)
            .WithTags("Authentication")
            .WithName("CompleteGoogleProfile")
            .RequireRateLimiting(RateLimitPolicy)
            .RequireEnmaAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> StartAsync(
        HttpContext httpContext,
        GoogleAuthenticationAvailability availability,
        IOrganizationInvitationTokenService invitationTokenService,
        CancellationToken cancellationToken)
    {
        if (!availability.Enabled)
        {
            return TypedResults.NotFound();
        }

        if (!httpContext.Request.HasFormContentType)
        {
            return TypedResults.BadRequest();
        }

        IFormCollection form = await httpContext.Request.ReadFormAsync(
            cancellationToken);
        string? invitationToken = form["invitationToken"].FirstOrDefault();
        if (invitationToken is not null &&
            (!invitationTokenService.TryHashToken(invitationToken, out var hash) ||
                hash is null))
        {
            return TypedResults.Redirect("/login?google=failed");
        }

        var properties = new AuthenticationProperties
        {
            RedirectUri = CompletePath
        };
        if (invitationToken is not null)
        {
            properties.Items[ExternalAuthenticationDefaults.InvitationItem] =
                invitationToken;
        }

        return TypedResults.Challenge(
            properties,
            [ExternalAuthenticationDefaults.GoogleScheme]);
    }

    private static async Task<IResult> CompleteAsync(
        HttpContext httpContext,
        GoogleAuthenticationAvailability availability,
        ExternalAuthenticationUseCase useCase,
        CancellationToken cancellationToken)
    {
        AuthenticateResult external = await httpContext.AuthenticateAsync(
            ExternalAuthenticationDefaults.CookieScheme);
        if (!external.Succeeded || external.Principal is null)
        {
            return TypedResults.Redirect("/login?google=failed");
        }

        string? subject = external.Principal.FindFirstValue(
            ExternalAuthenticationDefaults.GoogleSubjectClaim);
        string? email = external.Principal.FindFirstValue(
            ExternalAuthenticationDefaults.GoogleEmailClaim);
        string? name = external.Principal.FindFirstValue(
            ExternalAuthenticationDefaults.GoogleNameClaim);
        bool emailVerified = string.Equals(
            external.Principal.FindFirstValue(
                ExternalAuthenticationDefaults.GoogleEmailVerifiedClaim),
            "true",
            StringComparison.OrdinalIgnoreCase);
        string? invitationToken = null;
        external.Properties?.Items.TryGetValue(
            ExternalAuthenticationDefaults.InvitationItem,
            out invitationToken);

        ExternalAuthenticationResult result = await useCase.CompleteAsync(
            ExternalAuthenticationDefaults.GoogleProvider,
            subject,
            email,
            emailVerified,
            name,
            invitationToken,
            cancellationToken);

        if (result.Status == ExternalAuthenticationStatus.LinkRequired)
        {
            return TypedResults.Redirect(BuildFrontendTarget(
                "/login?google=link-required",
                invitationToken,
                availability.FrontendOrigin));
        }

        if (result.Status == ExternalAuthenticationStatus.ProfileRequired)
        {
            return TypedResults.Redirect(BuildFrontendTarget(
                "/register?google=profile-required",
                invitationToken,
                availability.FrontendOrigin));
        }

        if (result.Status == ExternalAuthenticationStatus.Succeeded)
        {
            if (invitationToken is not null)
            {
                await httpContext.SignOutAsync(
                    ExternalAuthenticationDefaults.CookieScheme);
            }

            httpContext.Response.Cookies.Append(
                AuthenticationCookies.SessionName,
                result.SessionHandle ?? throw new InvalidOperationException(
                    "Successful Google authentication requires a session handle."),
                AuthenticationCookies.CreateSessionOptions());
            return TypedResults.Redirect(BuildFrontendTarget(
                invitationToken is null
                    ? "/organizations"
                    : "/accept-invitation",
                invitationToken,
                availability.FrontendOrigin));
        }

        await httpContext.SignOutAsync(
            ExternalAuthenticationDefaults.CookieScheme);

        string target = result.Status switch
        {
            ExternalAuthenticationStatus.WrongInvitationRecipient =>
                "/register?google=wrong-invitation",
            ExternalAuthenticationStatus.InvalidInvitation =>
                "/register?google=invalid-invitation",
            _ => "/login?google=failed"
        };
        return TypedResults.Redirect(BuildFrontendTarget(
            target,
            invitationToken,
            availability.FrontendOrigin));
    }

    private static async Task<IResult> CompleteProfileAsync(
        CompleteGoogleProfileRequest request,
        HttpContext httpContext,
        ExternalAuthenticationUseCase useCase,
        CancellationToken cancellationToken)
    {
        AuthenticateResult external = await httpContext.AuthenticateAsync(
            ExternalAuthenticationDefaults.CookieScheme);
        if (!TryReadIdentity(
                external,
                out string? subject,
                out string? email,
                out bool emailVerified,
                out string? invitationToken))
        {
            return TypedResults.Unauthorized();
        }

        ExternalAuthenticationResult result = await useCase.CompleteAsync(
            ExternalAuthenticationDefaults.GoogleProvider,
            subject,
            email,
            emailVerified,
            request.Name,
            invitationToken,
            cancellationToken);
        if (result.Status != ExternalAuthenticationStatus.Succeeded)
        {
            if (result.Status is ExternalAuthenticationStatus.InvalidInvitation or
                ExternalAuthenticationStatus.WrongInvitationRecipient)
            {
                await httpContext.SignOutAsync(
                    ExternalAuthenticationDefaults.CookieScheme);
            }

            return result.Status switch
            {
                ExternalAuthenticationStatus.LinkRequired =>
                    TypedResults.Conflict(),
                ExternalAuthenticationStatus.InvalidInvitation =>
                    TypedResults.StatusCode(StatusCodes.Status410Gone),
                ExternalAuthenticationStatus.WrongInvitationRecipient =>
                    TypedResults.Forbid(),
                _ => TypedResults.UnprocessableEntity()
            };
        }

        if (invitationToken is not null)
        {
            await httpContext.SignOutAsync(
                ExternalAuthenticationDefaults.CookieScheme);
        }

        httpContext.Response.Cookies.Append(
            AuthenticationCookies.SessionName,
            result.SessionHandle ?? throw new InvalidOperationException(
                "Successful Google authentication requires a session handle."),
            AuthenticationCookies.CreateSessionOptions());
        return TypedResults.NoContent();
    }

    private static bool TryReadIdentity(
        AuthenticateResult external,
        out string? subject,
        out string? email,
        out bool emailVerified,
        out string? invitationToken)
    {
        subject = external.Principal?.FindFirstValue(
            ExternalAuthenticationDefaults.GoogleSubjectClaim);
        email = external.Principal?.FindFirstValue(
            ExternalAuthenticationDefaults.GoogleEmailClaim);
        emailVerified = string.Equals(
            external.Principal?.FindFirstValue(
                ExternalAuthenticationDefaults.GoogleEmailVerifiedClaim),
            "true",
            StringComparison.OrdinalIgnoreCase);
        invitationToken = null;
        external.Properties?.Items.TryGetValue(
            ExternalAuthenticationDefaults.InvitationItem,
            out invitationToken);
        return external.Succeeded &&
            external.Principal is not null &&
            subject is not null &&
            email is not null &&
            emailVerified;
    }

    private static string BuildFrontendTarget(
        string path,
        string? invitationToken,
        Uri? frontendOrigin)
    {
        string target = invitationToken is null
            ? path
            : $"{path}#token={Uri.EscapeDataString(invitationToken)}";
        return frontendOrigin is null
            ? target
            : new Uri(frontendOrigin, target).AbsoluteUri;
    }
}
