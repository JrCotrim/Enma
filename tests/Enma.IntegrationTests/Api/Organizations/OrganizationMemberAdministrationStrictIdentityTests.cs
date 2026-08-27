using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Enma.Application.Authorization;
using Enma.Application.Organizations.Members.List;
using Enma.Application.Organizations.Members.Lifecycle;
using Enma.Application.Organizations.Members.Role;
using Enma.Application.Organizations.UpdateName;
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
public sealed class OrganizationMemberAdministrationStrictIdentityTests(
    PostgreSqlFixture fixture)
{
    private const string TestScheme = "UntrustedTeamAdministrationPrincipal";

    private static readonly Guid UserId = Guid.Parse(
        "e9200934-c7a5-4cc5-878f-e39d75732962");
    private static readonly Guid OrganizationId = Guid.Parse(
        "79d4ae9f-47df-4c31-9d45-0cecfb3b6d28");

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
            "7020cc53-59b0-4d72-b52c-32af61c6eb78"
        }
    };

    [Theory]
    [MemberData(nameof(UntrustedUserIdentifiers))]
    public async Task List_WithUntrustedNameIdentifier_FailsPolicyWithoutQueries(
        string[] identifierValues)
    {
        var accessLookup = new RecordingOrganizationAccessLookup();
        var memberQueries = new RecordingAdministrationQueries();
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
            services.RemoveAll<IOrganizationMemberAdministrationQueries>();
            services.AddSingleton<IOrganizationMemberAdministrationQueries>(
                memberQueries);
        });
        using HttpClient client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                HandleCookies = false
            });

        using HttpResponseMessage response = await client.GetAsync(
            $"/api/organizations/{OrganizationId:D}/members");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, accessLookup.CallCount);
        Assert.Equal(0, memberQueries.CallCount);
    }

    [Theory]
    [MemberData(nameof(UntrustedUserIdentifiers))]
    public async Task ChangeRole_WithUntrustedNameIdentifier_FailsPolicyWithoutMutation(
        string[] identifierValues)
    {
        var accessLookup = new RecordingOrganizationAccessLookup();
        var persistence = new RecordingRoleMutationPersistence();
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
            services.RemoveAll<IOrganizationMemberRoleMutationPersistence>();
            services.AddSingleton<IOrganizationMemberRoleMutationPersistence>(
                persistence);
        });
        using HttpClient client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                HandleCookies = false
            });
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/organizations/{OrganizationId:D}/members/{Guid.NewGuid():D}/role")
        {
            Content = JsonContent.Create(new
            {
                role = "Administrator",
                expectedCurrentRole = "Member"
            })
        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, accessLookup.CallCount);
        Assert.Equal(0, persistence.CallCount);
    }

    [Theory]
    [MemberData(nameof(UntrustedUserIdentifiers))]
    public async Task UpdateName_WithUntrustedNameIdentifier_FailsPolicyWithoutMutation(
        string[] identifierValues)
    {
        var accessLookup = new RecordingOrganizationAccessLookup();
        var persistence = new RecordingNameMutationPersistence();
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
            services.RemoveAll<IOrganizationNameMutationPersistence>();
            services.AddSingleton<IOrganizationNameMutationPersistence>(persistence);
        });
        using HttpClient client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                HandleCookies = false
            });
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/organizations/{OrganizationId:D}")
        {
            Content = JsonContent.Create(new { name = "Denied Legal" })
        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, accessLookup.CallCount);
        Assert.Equal(0, persistence.CallCount);
    }

    [Theory]
    [MemberData(nameof(UntrustedLifecycleRequests))]
    public async Task Lifecycle_WithUntrustedNameIdentifier_FailsPolicyWithoutMutation(
        string[] identifierValues,
        string operation)
    {
        var accessLookup = new RecordingOrganizationAccessLookup();
        var persistence = new RecordingLifecycleMutationPersistence();
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
            services.RemoveAll<IOrganizationMemberLifecycleMutationPersistence>();
            services.AddSingleton<IOrganizationMemberLifecycleMutationPersistence>(
                persistence);
        });
        using HttpClient client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                HandleCookies = false
            });
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/organizations/{OrganizationId:D}/members/{Guid.NewGuid():D}/{operation}");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, accessLookup.CallCount);
        Assert.Equal(0, persistence.CallCount);
    }

    public static IEnumerable<object[]> UntrustedLifecycleRequests()
    {
        foreach (string[] identifierValues in UntrustedUserIdentifiers)
        {
            yield return [identifierValues, "deactivate"];
            yield return [identifierValues, "reactivate"];
        }
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

    private sealed class RecordingAdministrationQueries
        : IOrganizationMemberAdministrationQueries
    {
        public int CallCount { get; private set; }

        public Task<OrganizationMemberAdministrationPage> ListAsync(
            OrganizationMemberAdministrationQuery query,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new OrganizationMemberAdministrationPage([], 0));
        }
    }

    private sealed class RecordingRoleMutationPersistence
        : IOrganizationMemberRoleMutationPersistence
    {
        public int CallCount { get; private set; }

        public Task<OrganizationMemberRoleMutationPersistenceResult> ExecuteAsync(
            OrganizationMemberRoleMutationPersistenceRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(
                OrganizationMemberRoleMutationPersistenceResult.Succeeded);
        }
    }

    private sealed class RecordingLifecycleMutationPersistence
        : IOrganizationMemberLifecycleMutationPersistence
    {
        public int CallCount { get; private set; }

        public Task<OrganizationMemberLifecycleMutationPersistenceResult>
            ExecuteAsync(
                OrganizationMemberLifecycleMutationPersistenceRequest request,
                CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(
                OrganizationMemberLifecycleMutationPersistenceResult.Succeeded);
        }
    }

    private sealed class RecordingNameMutationPersistence
        : IOrganizationNameMutationPersistence
    {
        public int CallCount { get; private set; }

        public Task<OrganizationNameMutationPersistenceResult> ExecuteAsync(
            OrganizationNameMutationPersistenceRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(
                OrganizationNameMutationPersistenceResult.Succeeded);
        }
    }
}
