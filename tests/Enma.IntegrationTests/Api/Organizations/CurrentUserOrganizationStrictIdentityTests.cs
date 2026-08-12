using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Enma.Application.Organizations.CurrentUser;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Api.Organizations;

[Collection(PostgreSqlCollection.Name)]
public sealed class CurrentUserOrganizationStrictIdentityTests(
    PostgreSqlFixture fixture)
{
    private const string RequestPath = "/api/me/organizations";
    private const string TestScheme = "UntrustedPrincipal";

    private static readonly Guid UserId = Guid.Parse(
        "54da68c7-3f3b-46e7-bf8d-bb34e63bb06f");

    public static TheoryData<string[]> UntrustedUserIdentifiers => new()
    {
        Array.Empty<string>(),
        new[] { "not-a-guid" },
        new[] { Guid.Empty.ToString("D") },
        new[] { UserId.ToString("N") },
        new[] { UserId.ToString("D"), UserId.ToString("D") },
        new[]
        {
            UserId.ToString("D"),
            "78eb13dc-f65a-46c4-8dda-6595ed3614b9"
        }
    };

    [Theory]
    [MemberData(nameof(UntrustedUserIdentifiers))]
    public async Task Get_WithUntrustedNameIdentifier_FailsClosedWithoutQuery(
        string[] identifierValues)
    {
        var queries = new RecordingCurrentUserOrganizationQueries();
        await using var factory = new EnmaApiFactory(fixture, services =>
        {
            services.AddSingleton(new TestPrincipalClaims(identifierValues));
            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestScheme;
                    options.DefaultChallengeScheme = TestScheme;
                })
                .AddScheme<
                    AuthenticationSchemeOptions,
                    UntrustedPrincipalAuthenticationHandler>(
                        TestScheme,
                        _ => { });
            services.RemoveAll<ICurrentUserOrganizationQueries>();
            services.AddSingleton<ICurrentUserOrganizationQueries>(queries);
        });
        using HttpClient client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                HandleCookies = false
            });

        using HttpResponseMessage response = await client.GetAsync(RequestPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, queries.CallCount);
    }

    private sealed record TestPrincipalClaims(string[] IdentifierValues);

    private sealed class UntrustedPrincipalAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TestPrincipalClaims claims)
        : AuthenticationHandler<AuthenticationSchemeOptions>(
            options,
            logger,
            encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Claim[] identifierClaims = claims.IdentifierValues
                .Select(value => new Claim(ClaimTypes.NameIdentifier, value))
                .ToArray();
            var identity = new ClaimsIdentity(identifierClaims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class RecordingCurrentUserOrganizationQueries
        : ICurrentUserOrganizationQueries
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<CurrentUserOrganizationReadModel>>
            ListAccessibleAsync(
                Guid userId,
                CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<CurrentUserOrganizationReadModel>>(
                []);
        }
    }
}
