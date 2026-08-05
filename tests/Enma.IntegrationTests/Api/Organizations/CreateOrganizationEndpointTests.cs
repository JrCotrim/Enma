using System.Net;
using System.Net.Http.Json;
using Enma.Api.Contracts.Organizations;
using Enma.Domain.Organizations;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Api.Organizations;

[Collection(PostgreSqlCollection.Name)]
public sealed class CreateOrganizationEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset SeedCreatedAt = new(
        2026,
        1,
        2,
        3,
        4,
        5,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public CreateOrganizationEndpointTests(PostgreSqlFixture fixture)
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
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        await dbContext.Organizations.ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Post_WithValidRequest_ReturnsCreatedOrganization()
    {
        CreateOrganizationRequest request = new(
            "  Enma Legal  ",
            "  ENMA-LEGAL  ");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/organizations",
            request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            "application/json",
            response.Content.Headers.ContentType?.MediaType);

        CreateOrganizationResponse? organization =
            await response.Content.ReadFromJsonAsync<CreateOrganizationResponse>();
        Assert.NotNull(organization);
        Uri? location = response.Headers.Location;
        Assert.NotNull(location);
        string locationPath = location.IsAbsoluteUri
            ? location.AbsolutePath
            : location.OriginalString;
        Assert.Equal($"/api/organizations/{organization.Id}", locationPath);
        Assert.NotEqual(Guid.Empty, organization.Id);
        Assert.Equal("Enma Legal", organization.Name);
        Assert.Equal("enma-legal", organization.Slug);
        Assert.True(organization.IsActive);
        Assert.NotEqual(default, organization.CreatedAt);
        Assert.Equal(TimeSpan.Zero, organization.CreatedAt.Offset);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization persistedOrganization = await dbContext.Organizations
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(organization.Id, persistedOrganization.Id);
        Assert.Equal(organization.Name, persistedOrganization.Name);
        Assert.Equal(organization.Slug, persistedOrganization.Slug);
        Assert.Equal(organization.IsActive, persistedOrganization.IsActive);
        TimeSpan createdAtDifference =
            (organization.CreatedAt - persistedOrganization.CreatedAt).Duration();
        Assert.InRange(
            createdAtDifference,
            TimeSpan.Zero,
            TimeSpan.FromTicks(9));

        HttpResponseMessage getResponse = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        GetOrganizationResponse? retrievedOrganization =
            await getResponse.Content.ReadFromJsonAsync<GetOrganizationResponse>();
        Assert.NotNull(retrievedOrganization);
        Assert.Equal(organization.Id, retrievedOrganization.Id);
        Assert.Equal(organization.Name, retrievedOrganization.Name);
        Assert.Equal(organization.Slug, retrievedOrganization.Slug);
        Assert.Equal(organization.IsActive, retrievedOrganization.IsActive);
        TimeSpan retrievedCreatedAtDifference =
            (organization.CreatedAt - retrievedOrganization.CreatedAt).Duration();
        Assert.InRange(
            retrievedCreatedAtDifference,
            TimeSpan.Zero,
            TimeSpan.FromTicks(9));
    }

    [Fact]
    public async Task Post_WithExistingNormalizedSlug_ReturnsConflict()
    {
        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.Organizations.Add(new Organization(
                "Existing Legal",
                "shared-slug",
                SeedCreatedAt));
            await seedContext.SaveChangesAsync();
        }

        CreateOrganizationRequest request = new(
            "Another Legal",
            "  SHARED-SLUG  ");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/organizations",
            request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        ProblemDetails? problemDetails =
            await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problemDetails);
        Assert.Equal(StatusCodes.Status409Conflict, problemDetails.Status);
        Assert.Equal("Organization slug conflict", problemDetails.Title);
        Assert.Contains("shared-slug", problemDetails.Detail);
        AssertTraceId(problemDetails);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.Organizations.CountAsync());
    }

    [Fact]
    public async Task Post_WithInvalidName_ReturnsBadRequest()
    {
        CreateOrganizationRequest request = new(
            "   ",
            "valid-slug");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/organizations",
            request);

        await AssertBadRequestAsync(
            response,
            "Organization name cannot be null, empty, or whitespace.");
        await AssertOrganizationsTableIsEmptyAsync();
    }

    [Fact]
    public async Task Post_WithInvalidSlug_ReturnsBadRequest()
    {
        CreateOrganizationRequest request = new(
            "Enma Legal",
            "invalid slug");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/organizations",
            request);

        await AssertBadRequestAsync(
            response,
            "Organization slug must contain only lowercase letters, numbers, and single hyphens, and must start and end with a letter or number.");
        await AssertOrganizationsTableIsEmptyAsync();
    }

    private async Task AssertBadRequestAsync(
        HttpResponseMessage response,
        string expectedDetail)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        ProblemDetails? problemDetails =
            await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problemDetails);
        Assert.Equal(StatusCodes.Status400BadRequest, problemDetails.Status);
        Assert.Equal("Invalid organization data", problemDetails.Title);
        Assert.Contains(expectedDetail, problemDetails.Detail);
        AssertTraceId(problemDetails);
    }

    private async Task AssertOrganizationsTableIsEmptyAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.Organizations.CountAsync());
    }

    private static void AssertTraceId(ProblemDetails problemDetails)
    {
        Assert.True(
            problemDetails.Extensions.TryGetValue("traceId", out object? traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId?.ToString()));
    }
}
