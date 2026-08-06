using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Enma.Api.Contracts.Onboarding;
using Enma.Api.Contracts.Organizations;
using Enma.Application.Security;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Api;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Enma.IntegrationTests.Api.Onboarding;

[Collection(PostgreSqlCollection.Name)]
public sealed class RegisterOrganizationOwnerEndpointTests : IAsyncLifetime
{
    private const string SyntheticPassword = "HttpTest!Owner42";
    private const string InvalidSyntheticPassword = "Short!7";
    private const string SafeDuplicateEmailMessage =
        "A user with the provided email already exists.";
    private const string RequestPath = "/api/onboarding/register";

    private static readonly DateTimeOffset SeedCreatedAt = new(
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

    public RegisterOrganizationOwnerEndpointTests(PostgreSqlFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        this.fixture = fixture;
        factory = new EnmaApiFactory(fixture);
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Post_WithValidRequest_ReturnsCreatedResponseAndLocation()
    {
        RegisterOrganizationOwnerRequest request = CreateValidRequest();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            "application/json",
            response.Content.Headers.ContentType?.MediaType);
        RegisterOrganizationOwnerResponse? onboarding =
            await response.Content
                .ReadFromJsonAsync<RegisterOrganizationOwnerResponse>();
        Assert.NotNull(onboarding);
        Assert.Equal("Enma Legal", onboarding.OrganizationName);
        Assert.Equal("enma-legal", onboarding.OrganizationSlug);
        Assert.Equal("Ana Silva", onboarding.UserName);
        Assert.Equal("owner@example.com", onboarding.UserEmail);
        Assert.NotEqual(Guid.Empty, onboarding.OrganizationId);
        Assert.NotEqual(Guid.Empty, onboarding.UserId);
        Assert.NotEqual(Guid.Empty, onboarding.MembershipId);
        Assert.Equal(
            3,
            new HashSet<Guid>
            {
                onboarding.OrganizationId,
                onboarding.UserId,
                onboarding.MembershipId
            }.Count);
        Assert.Equal("Owner", onboarding.Role);
        Assert.NotEqual(default, onboarding.CreatedAt);
        Assert.Equal(TimeSpan.Zero, onboarding.CreatedAt.Offset);

        Uri? location = response.Headers.Location;
        Assert.NotNull(location);
        string locationPath = location.IsAbsoluteUri
            ? location.AbsolutePath
            : location.OriginalString;
        Assert.Equal(
            $"/api/organizations/{onboarding.OrganizationId}",
            locationPath);

        HttpResponseMessage getResponse = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        GetOrganizationResponse? organization =
            await getResponse.Content.ReadFromJsonAsync<GetOrganizationResponse>();
        Assert.NotNull(organization);
        Assert.Equal(onboarding.OrganizationId, organization.Id);
        Assert.Equal(onboarding.OrganizationName, organization.Name);
        Assert.Equal(onboarding.OrganizationSlug, organization.Slug);
    }

    [Fact]
    public async Task Post_WithValidRequest_PersistsCompleteOnboardingAndVerifiableCredential()
    {
        RegisterOrganizationOwnerRequest request = CreateValidRequest();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        RegisterOrganizationOwnerResponse? onboarding =
            await response.Content
                .ReadFromJsonAsync<RegisterOrganizationOwnerResponse>();
        Assert.NotNull(onboarding);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = await dbContext.Organizations
            .AsNoTracking()
            .SingleAsync();
        User user = await dbContext.Users
            .AsNoTracking()
            .SingleAsync();
        UserCredential credential = await dbContext.UserCredentials
            .AsNoTracking()
            .SingleAsync();
        OrganizationMembership membership =
            await dbContext.OrganizationMemberships
                .AsNoTracking()
                .SingleAsync();

        Assert.Equal(onboarding.OrganizationId, organization.Id);
        Assert.Equal("Enma Legal", organization.Name);
        Assert.Equal("enma-legal", organization.Slug);
        Assert.True(organization.IsActive);
        Assert.Equal(onboarding.UserId, user.Id);
        Assert.Equal("Ana Silva", user.Name);
        Assert.Equal("owner@example.com", user.Email);
        Assert.True(user.IsActive);
        Assert.Equal(onboarding.MembershipId, membership.Id);
        Assert.Equal(organization.Id, membership.OrganizationId);
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(OrganizationRole.Owner, membership.Role);
        Assert.True(membership.IsActive);
        Assert.Equal(user.Id, credential.UserId);
        Assert.False(string.IsNullOrWhiteSpace(credential.PasswordHash));
        Assert.NotEqual(request.Password, credential.PasswordHash);

        using IServiceScope scope = factory.Services.CreateScope();
        IPasswordHasher passwordHasher =
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        Assert.Equal(
            PasswordVerificationResult.Success,
            passwordHasher.VerifyHashedPassword(
                user,
                credential.PasswordHash,
                request.Password));
    }

    [Fact]
    public async Task Post_WithInvalidOrganizationName_ReturnsBadRequestWithoutWrites()
    {
        RegisterOrganizationOwnerRequest request = CreateValidRequest(
            organizationName: "   ");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "Invalid onboarding request");
        await AssertAllTablesEmptyAsync();
    }

    [Fact]
    public async Task Post_WithInvalidOwnerEmail_ReturnsBadRequestWithoutWrites()
    {
        RegisterOrganizationOwnerRequest request = CreateValidRequest(
            ownerEmail: "invalid-email");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "Invalid onboarding request");
        await AssertAllTablesEmptyAsync();
    }

    [Fact]
    public async Task Post_WithInvalidPassword_ReturnsBadRequestWithoutWritesOrPasswordExposure()
    {
        RegisterOrganizationOwnerRequest request = CreateValidRequest(
            password: InvalidSyntheticPassword);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        (ProblemDetails problemDetails, string rawResponse) =
            await AssertProblemAsync(
                response,
                HttpStatusCode.BadRequest,
                "Invalid onboarding request");
        Assert.Contains(PasswordPolicyErrors.PasswordTooShort, problemDetails.Detail);
        Assert.DoesNotContain(
            request.Password,
            rawResponse,
            StringComparison.Ordinal);
        await AssertAllTablesEmptyAsync();
    }

    [Fact]
    public async Task Post_WithExistingSlug_ReturnsConflictWithoutNewWrites()
    {
        Organization seededOrganization = new(
            "Existing Legal",
            "enma-legal",
            SeedCreatedAt);
        await SeedAsync(seededOrganization);
        RegisterOrganizationOwnerRequest request = CreateValidRequest();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        await AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "Onboarding conflict");

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = await dbContext.Organizations
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(seededOrganization.Id, organization.Id);
        Assert.Equal(0, await dbContext.Users.CountAsync());
        Assert.Equal(0, await dbContext.UserCredentials.CountAsync());
        Assert.Equal(0, await dbContext.OrganizationMemberships.CountAsync());
    }

    [Fact]
    public async Task Post_WithExistingEmail_ReturnsConflictWithoutNewWritesOrEmailExposure()
    {
        User seededUser = new(
            "Existing User",
            "owner@example.com",
            SeedCreatedAt);
        await SeedAsync(seededUser);
        RegisterOrganizationOwnerRequest request = CreateValidRequest(
            organizationSlug: "new-legal",
            ownerEmail: "owner@example.com");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        (ProblemDetails problemDetails, string rawResponse) =
            await AssertProblemAsync(
                response,
                HttpStatusCode.Conflict,
                "Onboarding conflict");
        Assert.Equal(SafeDuplicateEmailMessage, problemDetails.Detail);
        Assert.DoesNotContain(
            request.OwnerEmail,
            rawResponse,
            StringComparison.OrdinalIgnoreCase);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = await dbContext.Users.AsNoTracking().SingleAsync();
        Assert.Equal(seededUser.Id, user.Id);
        Assert.Equal(0, await dbContext.Organizations.CountAsync());
        Assert.Equal(0, await dbContext.UserCredentials.CountAsync());
        Assert.Equal(0, await dbContext.OrganizationMemberships.CountAsync());
    }

    [Fact]
    public async Task Post_ResponseContract_DoesNotExposePasswordOrCredentialData()
    {
        RegisterOrganizationOwnerRequest request = CreateValidRequest();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        string rawResponse = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("password", rawResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passwordHash", rawResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", rawResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "passwordChangedAt",
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            request.Password,
            rawResponse,
            StringComparison.Ordinal);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        UserCredential credential = await dbContext.UserCredentials
            .AsNoTracking()
            .SingleAsync();
        Assert.DoesNotContain(
            credential.PasswordHash,
            rawResponse,
            StringComparison.Ordinal);

        string requestDescription = Assert.IsType<string>(request.ToString());
        Assert.DoesNotContain(
            request.Password,
            requestDescription,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            request.OwnerEmail,
            requestDescription,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            request.OrganizationName,
            requestDescription,
            StringComparison.Ordinal);
    }

    private static RegisterOrganizationOwnerRequest CreateValidRequest(
        string organizationName = "  Enma Legal  ",
        string organizationSlug = "  ENMA-LEGAL  ",
        string ownerEmail = "  OWNER@EXAMPLE.COM  ",
        string password = SyntheticPassword)
    {
        return new RegisterOrganizationOwnerRequest
        {
            OrganizationName = organizationName,
            OrganizationSlug = organizationSlug,
            OwnerName = "  Ana Silva  ",
            OwnerEmail = ownerEmail,
            Password = password
        };
    }

    private static async Task<(ProblemDetails ProblemDetails, string RawResponse)>
        AssertProblemAsync(
            HttpResponseMessage response,
            HttpStatusCode expectedStatusCode,
            string expectedTitle)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        string rawResponse = await response.Content.ReadAsStringAsync();
        ProblemDetails? problemDetails = JsonSerializer.Deserialize<ProblemDetails>(
            rawResponse,
            JsonSerializerOptions.Web);
        Assert.NotNull(problemDetails);
        Assert.Equal((int)expectedStatusCode, problemDetails.Status);
        Assert.Equal(expectedTitle, problemDetails.Title);
        Assert.Equal(RequestPath, problemDetails.Instance);
        Assert.True(
            problemDetails.Extensions.TryGetValue("traceId", out object? traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId?.ToString()));

        return (problemDetails, rawResponse);
    }

    private async Task AssertAllTablesEmptyAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.Organizations.CountAsync());
        Assert.Equal(0, await dbContext.Users.CountAsync());
        Assert.Equal(0, await dbContext.UserCredentials.CountAsync());
        Assert.Equal(0, await dbContext.OrganizationMemberships.CountAsync());
    }

    private async Task SeedAsync(object entity)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Add(entity);
        await dbContext.SaveChangesAsync();
    }
}
