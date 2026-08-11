using Enma.Application.Authorization;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Application.Authorization;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationAccessAuthorizationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        11,
        12,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AuthorizeAsync_WithMembershipOnlyInFirstOrganization_IsolatesOrganizations()
    {
        Organization firstOrganization = CreateOrganization(
            "First Legal",
            "first-legal");
        Organization secondOrganization = CreateOrganization(
            "Second Legal",
            "second-legal");
        User user = CreateUser("First User", "first@example.test");
        OrganizationMembership membership = new(
            firstOrganization.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);
        await SeedAsync(firstOrganization, secondOrganization, user, membership);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationAccessAuthorization authorization = CreateAuthorization(dbContext);

        OrganizationAccessAuthorizationResult firstResult =
            await authorization.AuthorizeAsync(user.Id, firstOrganization.Id);
        OrganizationAccessAuthorizationResult secondResult =
            await authorization.AuthorizeAsync(user.Id, secondOrganization.Id);

        AssertAllowed(firstResult, OrganizationRole.Owner);
        AssertDenied(secondResult);
    }

    [Fact]
    public async Task AuthorizeAsync_WithDifferentRolesInOrganizations_ReturnsOrganizationScopedRoles()
    {
        Organization firstOrganization = CreateOrganization(
            "First Legal",
            "first-legal");
        Organization secondOrganization = CreateOrganization(
            "Second Legal",
            "second-legal");
        User user = CreateUser("First User", "first@example.test");
        OrganizationMembership firstMembership = new(
            firstOrganization.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);
        OrganizationMembership secondMembership = new(
            secondOrganization.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt);
        await SeedAsync(
            firstOrganization,
            secondOrganization,
            user,
            firstMembership,
            secondMembership);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationAccessAuthorization authorization = CreateAuthorization(dbContext);

        OrganizationAccessAuthorizationResult firstResult =
            await authorization.AuthorizeAsync(user.Id, firstOrganization.Id);
        OrganizationAccessAuthorizationResult secondResult =
            await authorization.AuthorizeAsync(user.Id, secondOrganization.Id);

        AssertAllowed(firstResult, OrganizationRole.Owner);
        AssertAllowed(secondResult, OrganizationRole.Member);
    }

    [Fact]
    public async Task AuthorizeAsync_WithOnlyDifferentUsersMembership_ReturnsDenied()
    {
        Organization organization = CreateOrganization(
            "Second Legal",
            "second-legal");
        User firstUser = CreateUser("First User", "first@example.test");
        User secondUser = CreateUser("Second User", "second@example.test");
        OrganizationMembership secondMembership = new(
            organization.Id,
            secondUser.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        await SeedAsync(organization, firstUser, secondUser, secondMembership);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationAccessAuthorization authorization = CreateAuthorization(dbContext);

        OrganizationAccessAuthorizationResult result =
            await authorization.AuthorizeAsync(firstUser.Id, organization.Id);

        AssertDenied(result);
    }

    [Fact]
    public async Task AuthorizeAsync_AfterPersistedRoleChange_ReturnsNewRoleOnNextEvaluation()
    {
        Organization organization = CreateOrganization(
            "First Legal",
            "first-legal");
        User user = CreateUser("First User", "first@example.test");
        OrganizationMembership membership = new(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);
        await SeedAsync(organization, user, membership);

        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        OrganizationAccessAuthorization authorization =
            CreateAuthorization(authorizationContext);

        OrganizationAccessAuthorizationResult initialResult =
            await authorization.AuthorizeAsync(user.Id, organization.Id);

        await using (EnmaDbContext mutationContext = fixture.CreateDbContext())
        {
            OrganizationMembership persistedMembership =
                await mutationContext.OrganizationMemberships.SingleAsync();
            persistedMembership.ChangeRole(OrganizationRole.Administrator);
            await mutationContext.SaveChangesAsync();
        }

        OrganizationAccessAuthorizationResult changedResult =
            await authorization.AuthorizeAsync(user.Id, organization.Id);

        AssertAllowed(initialResult, OrganizationRole.Owner);
        AssertAllowed(changedResult, OrganizationRole.Administrator);
    }

    [Fact]
    public async Task AuthorizeAsync_AfterPersistedMembershipDeactivation_ReturnsDeniedOnNextEvaluation()
    {
        Organization organization = CreateOrganization(
            "First Legal",
            "first-legal");
        User user = CreateUser("First User", "first@example.test");
        OrganizationMembership membership = new(
            organization.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt);
        await SeedAsync(organization, user, membership);

        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        OrganizationAccessAuthorization authorization =
            CreateAuthorization(authorizationContext);

        OrganizationAccessAuthorizationResult initialResult =
            await authorization.AuthorizeAsync(user.Id, organization.Id);

        await using (EnmaDbContext mutationContext = fixture.CreateDbContext())
        {
            OrganizationMembership persistedMembership =
                await mutationContext.OrganizationMemberships.SingleAsync();
            persistedMembership.Deactivate();
            await mutationContext.SaveChangesAsync();
        }

        OrganizationAccessAuthorizationResult deactivatedResult =
            await authorization.AuthorizeAsync(user.Id, organization.Id);

        AssertAllowed(initialResult, OrganizationRole.Member);
        AssertDenied(deactivatedResult);
    }

    [Fact]
    public async Task AuthorizeAsync_WithInactiveOrganization_ReturnsDenied()
    {
        Organization organization = CreateOrganization(
            "First Legal",
            "first-legal");
        organization.Deactivate();
        User user = CreateUser("First User", "first@example.test");
        OrganizationMembership membership = new(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);
        await SeedAsync(organization, user, membership);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationAccessAuthorization authorization = CreateAuthorization(dbContext);

        OrganizationAccessAuthorizationResult result =
            await authorization.AuthorizeAsync(user.Id, organization.Id);

        AssertDenied(result);
    }

    [Fact]
    public async Task AuthorizeAsync_WithEmptyOrMissingIdentifier_ReturnsGenericDenied()
    {
        Organization organization = CreateOrganization(
            "First Legal",
            "first-legal");
        User user = CreateUser("First User", "first@example.test");
        await SeedAsync(organization, user);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationAccessAuthorization authorization = CreateAuthorization(dbContext);

        OrganizationAccessAuthorizationResult emptyUserResult =
            await authorization.AuthorizeAsync(Guid.Empty, organization.Id);
        OrganizationAccessAuthorizationResult emptyOrganizationResult =
            await authorization.AuthorizeAsync(user.Id, Guid.Empty);
        OrganizationAccessAuthorizationResult missingUserResult =
            await authorization.AuthorizeAsync(Guid.NewGuid(), organization.Id);
        OrganizationAccessAuthorizationResult missingOrganizationResult =
            await authorization.AuthorizeAsync(user.Id, Guid.NewGuid());

        AssertDenied(emptyUserResult);
        AssertDenied(emptyOrganizationResult);
        AssertDenied(missingUserResult);
        AssertDenied(missingOrganizationResult);
    }

    private static OrganizationAccessAuthorization CreateAuthorization(
        EnmaDbContext dbContext)
    {
        return new OrganizationAccessAuthorization(
            new OrganizationAccessLookup(dbContext));
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static Organization CreateOrganization(string name, string slug)
    {
        return new Organization(name, slug, CreatedAt);
    }

    private static User CreateUser(string name, string email)
    {
        return new User(name, email, CreatedAt);
    }

    private static void AssertAllowed(
        OrganizationAccessAuthorizationResult result,
        OrganizationRole expectedRole)
    {
        Assert.Equal(OrganizationAccessAuthorizationStatus.Allowed, result.Status);
        Assert.Equal(expectedRole, result.Role);
    }

    private static void AssertDenied(OrganizationAccessAuthorizationResult result)
    {
        Assert.Equal(OrganizationAccessAuthorizationStatus.Denied, result.Status);
        Assert.Null(result.Role);
    }
}
