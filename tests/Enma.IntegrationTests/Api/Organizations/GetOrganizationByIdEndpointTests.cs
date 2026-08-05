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
public sealed class GetOrganizationByIdEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        5,
        12,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public GetOrganizationByIdEndpointTests(PostgreSqlFixture fixture)
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
    public async Task Get_WithExistingOrganization_ReturnsOrganization()
    {
        Organization organization = new("Enma Legal", "enma-legal", CreatedAt);
        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.Organizations.Add(organization);
            await seedContext.SaveChangesAsync();
        }

        string requestPath = $"/api/organizations/{organization.Id}";
        HttpResponseMessage response = await client.GetAsync(requestPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/json",
            response.Content.Headers.ContentType?.MediaType);
        GetOrganizationResponse? result =
            await response.Content.ReadFromJsonAsync<GetOrganizationResponse>();
        Assert.NotNull(result);
        Assert.Equal(organization.Id, result.Id);
        Assert.Equal("Enma Legal", result.Name);
        Assert.Equal("enma-legal", result.Slug);
        Assert.True(result.IsActive);
        Assert.Equal(CreatedAt, result.CreatedAt);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Organization persistedOrganization = await verificationContext.Organizations
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(organization.Id, persistedOrganization.Id);
        Assert.Equal("Enma Legal", persistedOrganization.Name);
        Assert.Equal("enma-legal", persistedOrganization.Slug);
        Assert.True(persistedOrganization.IsActive);
        Assert.Equal(CreatedAt, persistedOrganization.CreatedAt);
    }

    [Fact]
    public async Task Get_WithMissingOrganization_ReturnsNotFound()
    {
        Guid organizationId = Guid.NewGuid();
        string requestPath = $"/api/organizations/{organizationId}";

        HttpResponseMessage response = await client.GetAsync(requestPath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        ProblemDetails? problemDetails =
            await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problemDetails);
        Assert.Equal(StatusCodes.Status404NotFound, problemDetails.Status);
        Assert.Equal("Organization not found", problemDetails.Title);
        Assert.Contains(organizationId.ToString(), problemDetails.Detail);
        Assert.Equal(requestPath, problemDetails.Instance);
        AssertTraceId(problemDetails);
        await AssertOrganizationsTableIsEmptyAsync();
    }

    [Fact]
    public async Task Get_WithEmptyOrganizationId_ReturnsBadRequest()
    {
        const string RequestPath =
            "/api/organizations/00000000-0000-0000-0000-000000000000";

        HttpResponseMessage response = await client.GetAsync(RequestPath);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        ProblemDetails? problemDetails =
            await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problemDetails);
        Assert.Equal(StatusCodes.Status400BadRequest, problemDetails.Status);
        Assert.Equal("Invalid organization data", problemDetails.Title);
        Assert.Contains("Organization id cannot be empty.", problemDetails.Detail);
        Assert.Equal(RequestPath, problemDetails.Instance);
        AssertTraceId(problemDetails);
        await AssertOrganizationsTableIsEmptyAsync();
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
