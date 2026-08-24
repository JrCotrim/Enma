using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Enma.Application.Authorization;
using Enma.Domain.Organizations;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Api.Dashboard;

[Collection(PostgreSqlCollection.Name)]
public sealed class DashboardStrictIdentityTests(PostgreSqlFixture fixture)
{
    private const string TestScheme = "UntrustedDashboardPrincipal";

    private static readonly Guid UserId = Guid.Parse(
        "13bd25f4-7a9d-4138-9339-c2c036a9fd63");

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
            "1f258460-3295-4cc4-8550-e62755923294"
        }
    };

    [Theory]
    [MemberData(nameof(UntrustedUserIdentifiers))]
    public async Task GetDashboard_UntrustedNameIdentifier_FailsClosedBeforeLiveAccessLookup(
        string[] identifierValues)
    {
        var lookup = new RecordingOrganizationAccessLookup();
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
            services.RemoveAll<IOrganizationAccessLookup>();
            services.AddSingleton<IOrganizationAccessLookup>(lookup);
        });
        using HttpClient client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                HandleCookies = false
            });

        using HttpResponseMessage response = await client.GetAsync(
            $"/api/organizations/{Guid.NewGuid():D}/dashboard");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, lookup.CallCount);
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

    private sealed class RecordingOrganizationAccessLookup
        : IOrganizationAccessLookup
    {
        public int CallCount { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<OrganizationRole?>(OrganizationRole.Owner);
        }
    }
}
