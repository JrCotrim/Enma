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

namespace Enma.IntegrationTests.Api.Processes;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalProcessStrictIdentityTests(
    PostgreSqlFixture fixture)
{
    private const string TestScheme = "UntrustedProcessPrincipal";

    private static readonly Guid UserId = Guid.Parse(
        "47e53258-3519-4e67-86de-11a137848bab");

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
            "90e3f7b0-90bc-4391-a086-978c332ca897"
        }
    };

    [Theory]
    [MemberData(nameof(UntrustedUserIdentifiers))]
    public async Task ReadRoutes_WithUntrustedNameIdentifier_FailClosedBeforeLiveAccessLookup(
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

        Guid organizationId = Guid.NewGuid();
        string[] paths =
        [
            $"/api/organizations/{organizationId:D}/processes",
            $"/api/organizations/{organizationId:D}/processes/lookup"
        ];

        foreach (string path in paths)
        {
            using HttpResponseMessage response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal(
                string.Empty,
                await response.Content.ReadAsStringAsync());
        }

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
