using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Enma.Api.Endpoints.Authentication;
using Enma.Application.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Enma.Api.Authentication;

internal sealed class EnmaSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ValidateSessionUseCase validateSessionUseCase)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(
                AuthenticationCookies.SessionName,
                out string? rawHandle))
        {
            return AuthenticateResult.NoResult();
        }

        SessionValidationResult result = await validateSessionUseCase.ExecuteAsync(
            rawHandle,
            Context.RequestAborted);

        if (result.Status != SessionValidationResultStatus.Authenticated ||
            result.UserId is not Guid userId)
        {
            return AuthenticateResult.Fail("Session authentication failed.");
        }

        Claim[] claims =
        [
            new Claim(
                ClaimTypes.NameIdentifier,
                userId.ToString("D", CultureInfo.InvariantCulture))
        ];
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(
        AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(
        AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    }
}
