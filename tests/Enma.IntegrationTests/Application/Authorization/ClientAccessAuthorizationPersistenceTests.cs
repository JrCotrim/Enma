using Enma.Application.Authorization;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;

namespace Enma.IntegrationTests.Application.Authorization;

[Collection(PostgreSqlCollection.Name)]
public sealed class ClientAccessAuthorizationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        11,
        16,
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
    public async Task AuthorizeAsync_WithCrossTenantMatrix_EnforcesContextualOwnership()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "organization-b");
        User userA = CreateUser("User A", "user-a@example.test");
        User userB = CreateUser("User B", "user-b@example.test");
        OrganizationMembership ownerMembershipA = new(
            organizationA.Id,
            userA.Id,
            OrganizationRole.Owner,
            CreatedAt);
        OrganizationMembership membershipB = new(
            organizationB.Id,
            userB.Id,
            OrganizationRole.Member,
            CreatedAt);
        var clientA = new Client(
            organizationA.Id,
            "Client A",
            CreatedAt);
        var clientB = new Client(
            organizationB.Id,
            "Client B",
            CreatedAt);
        await SeedAsync(
            organizationA,
            organizationB,
            userA,
            userB,
            ownerMembershipA,
            membershipB,
            clientA,
            clientB);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        ClientAccessAuthorization authorization = CreateAuthorization(dbContext);

        ClientAccessAuthorizationResult sameTenantResult =
            await authorization.AuthorizeAsync(
                userA.Id,
                organizationA.Id,
                clientA.Id);
        ClientAccessAuthorizationResult ownerCrossTenantResult =
            await authorization.AuthorizeAsync(
                userA.Id,
                organizationA.Id,
                clientB.Id);
        ClientAccessAuthorizationResult missingResult =
            await authorization.AuthorizeAsync(
                userA.Id,
                organizationA.Id,
                Guid.NewGuid());
        ClientAccessAuthorizationResult differentUserResult =
            await authorization.AuthorizeAsync(
                userA.Id,
                organizationB.Id,
                clientB.Id);

        Assert.Equal(ClientAccessAuthorizationResult.Allowed, sameTenantResult);
        Assert.Equal(ClientAccessAuthorizationResult.Denied, ownerCrossTenantResult);
        Assert.Equal(ownerCrossTenantResult, missingResult);
        Assert.Equal(ClientAccessAuthorizationResult.Denied, differentUserResult);

        var userAMembershipB = new OrganizationMembership(
            organizationB.Id,
            userA.Id,
            OrganizationRole.Administrator,
            CreatedAt.AddMinutes(1));
        dbContext.OrganizationMemberships.Add(userAMembershipB);
        await dbContext.SaveChangesAsync();

        ClientAccessAuthorizationResult unchangedContextResult =
            await authorization.AuthorizeAsync(
                userA.Id,
                organizationA.Id,
                clientB.Id);
        ClientAccessAuthorizationResult changedContextResult =
            await authorization.AuthorizeAsync(
                userA.Id,
                organizationB.Id,
                clientB.Id);

        Assert.Equal(ClientAccessAuthorizationResult.Denied, unchangedContextResult);
        Assert.Equal(ClientAccessAuthorizationResult.Allowed, changedContextResult);
    }

    [Fact]
    public async Task AuthorizeAsync_WithInactiveSameTenantClient_RemainsAllowed()
    {
        Organization organization = CreateOrganization(
            "Organization A",
            "organization-a");
        User user = CreateUser("User A", "user-a@example.test");
        OrganizationMembership membership = new(
            organization.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt);
        var client = new Client(organization.Id, "Client A", CreatedAt);
        client.Deactivate();
        await SeedAsync(organization, user, membership, client);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        ClientAccessAuthorization authorization = CreateAuthorization(dbContext);

        ClientAccessAuthorizationResult result = await authorization.AuthorizeAsync(
            user.Id,
            organization.Id,
            client.Id);

        Assert.Equal(ClientAccessAuthorizationResult.Allowed, result);
    }

    private static ClientAccessAuthorization CreateAuthorization(
        EnmaDbContext dbContext)
    {
        return new ClientAccessAuthorization(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(dbContext)),
            new ClientOrganizationOwnershipLookup(dbContext));
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
}
