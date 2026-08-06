using System.Net;
using System.Net.Http.Json;
using Enma.Application.Organizations.Create;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Enma.IntegrationTests.Api.Organizations;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationCreationRouteRemovalTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public OrganizationCreationRouteRemovalTests(PostgreSqlFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        this.fixture = fixture;
        factory = new EnmaApiFactory(fixture);
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Post_ToOrganizationsRoute_ReturnsNotFoundWithoutWrites()
    {
        var request = new
        {
            name = "Synthetic Route Removal Legal",
            slug = "synthetic-route-removal-legal"
        };

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/organizations",
            request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(response.Headers.Location);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.Equal(0, await verificationContext.Organizations.CountAsync());
        Assert.Equal(0, await verificationContext.Users.CountAsync());
        Assert.Equal(0, await verificationContext.UserCredentials.CountAsync());
        Assert.Equal(
            0,
            await verificationContext.OrganizationMemberships.CountAsync());
    }

    [Fact]
    public void EndpointRouting_ContainsOrganizationGetButNoOrganizationPost()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        EndpointDataSource endpointDataSource =
            scope.ServiceProvider.GetRequiredService<EndpointDataSource>();
        IReadOnlyList<RouteEndpoint> routeEndpoints = endpointDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .ToList();

        Assert.DoesNotContain(
            routeEndpoints,
            endpoint => MatchesRouteAndMethod(
                endpoint,
                "/api/organizations",
                HttpMethods.Post));

        RouteEndpoint organizationGetEndpoint = Assert.Single(
            routeEndpoints,
            endpoint => MatchesRouteAndMethod(
                endpoint,
                "/api/organizations/{id:guid}",
                HttpMethods.Get));
        Assert.Equal(
            "GetOrganizationById",
            GetEndpointName(organizationGetEndpoint));

        RouteEndpoint onboardingEndpoint = Assert.Single(
            routeEndpoints,
            endpoint => MatchesRouteAndMethod(
                endpoint,
                "/api/onboarding/register",
                HttpMethods.Post));
        Assert.Equal(
            "RegisterOrganizationOwner",
            GetEndpointName(onboardingEndpoint));

        Assert.Null(
            scope.ServiceProvider.GetService<CreateOrganizationHandler>());
    }

    private static bool MatchesRouteAndMethod(
        RouteEndpoint endpoint,
        string routePattern,
        string httpMethod)
    {
        HttpMethodMetadata? httpMethodMetadata =
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>();

        return string.Equals(
                NormalizeRoutePattern(endpoint.RoutePattern.RawText),
                routePattern,
                StringComparison.Ordinal)
            && httpMethodMetadata?.HttpMethods.Contains(
                httpMethod,
                StringComparer.OrdinalIgnoreCase) is true;
    }

    private static string NormalizeRoutePattern(string? routePattern)
    {
        if (string.IsNullOrEmpty(routePattern))
        {
            return "/";
        }

        return routePattern.StartsWith('/')
            ? routePattern
            : $"/{routePattern}";
    }

    private static string? GetEndpointName(RouteEndpoint endpoint)
    {
        return endpoint.Metadata
            .GetMetadata<IEndpointNameMetadata>()?
            .EndpointName;
    }
}
