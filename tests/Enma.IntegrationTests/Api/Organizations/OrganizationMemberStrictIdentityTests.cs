using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Enma.Application.Authorization;
using Enma.Application.Organizations.Members.Lookup;
using Enma.Domain.Organizations;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Api.Organizations;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationMemberStrictIdentityTests(
    PostgreSqlFixture fixture)
{
    private const string TestScheme = "UntrustedMemberLookupPrincipal";

    private static readonly Guid UserId = Guid.Parse(
        "48243adf-e33d-4887-9d6f-4ed27f3901ac");

    private static readonly Guid OrganizationId = Guid.Parse(
        "3b4cd61f-7088-43df-835a-d62a28e5f423");

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
            "69c26ebc-f786-4322-b2bf-96973dba8298"
        }
    };

    [Theory]
    [MemberData(nameof(UntrustedUserIdentifiers))]
    public async Task Lookup_WithUntrustedNameIdentifier_ReturnsUnauthorizedWithoutQueries(
        string[] identifierValues)
    {
        var accessLookup = new RecordingOrganizationAccessLookup();
        var memberQueries = new RecordingOrganizationMemberLookupQueries();
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
            services.AddSingleton<IOrganizationAccessLookup>(accessLookup);
            services.RemoveAll<IOrganizationMemberLookupQueries>();
            services.AddSingleton<IOrganizationMemberLookupQueries>(memberQueries);
        });
        using HttpClient client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                HandleCookies = false
            });

        using HttpResponseMessage response = await client.GetAsync(
            $"/api/organizations/{OrganizationId:D}/members/lookup");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, accessLookup.CallCount);
        Assert.Equal(0, memberQueries.CallCount);
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
            return Task.FromResult<OrganizationRole?>(OrganizationRole.Member);
        }
    }

    private sealed class RecordingOrganizationMemberLookupQueries
        : IOrganizationMemberLookupQueries
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<OrganizationMemberLookupItem>> SearchAsync(
            Guid organizationId,
            string? search,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<OrganizationMemberLookupItem>>([]);
        }
    }
}
