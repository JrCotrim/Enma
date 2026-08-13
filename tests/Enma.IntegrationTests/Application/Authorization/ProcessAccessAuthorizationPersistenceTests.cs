using Enma.Application.Authorization;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;

namespace Enma.IntegrationTests.Application.Authorization;

[Collection(PostgreSqlCollection.Name)]
public sealed class ProcessAccessAuthorizationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        13,
        15,
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
    public async Task AuthorizeAsync_WithCrossTenantAndMissingProcesses_EnforcesContextualOwnership()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "organization-b");
        User user = new("User", "user@example.test", CreatedAt);
        var membershipA = new OrganizationMembership(
            organizationA.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt);
        var membershipB = new OrganizationMembership(
            organizationB.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var clientA = new Client(organizationA.Id, "Client A", CreatedAt);
        var clientB = new Client(organizationB.Id, "Client B", CreatedAt);
        var processA = new LegalProcess(
            organizationA.Id,
            clientA.Id,
            "Process A",
            CreatedAt);
        var processB = new LegalProcess(
            organizationB.Id,
            clientB.Id,
            "Process B",
            CreatedAt);
        await SeedAsync(
            organizationA,
            organizationB,
            user,
            membershipA,
            membershipB,
            clientA,
            clientB,
            processA,
            processB);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        ProcessAccessAuthorization authorization = CreateAuthorization(dbContext);

        ProcessAccessAuthorizationResult sameTenantResult =
            await authorization.AuthorizeAsync(
                user.Id,
                organizationA.Id,
                processA.Id);
        ProcessAccessAuthorizationResult crossTenantResult =
            await authorization.AuthorizeAsync(
                user.Id,
                organizationA.Id,
                processB.Id);
        ProcessAccessAuthorizationResult missingResult =
            await authorization.AuthorizeAsync(
                user.Id,
                organizationA.Id,
                Guid.NewGuid());
        ProcessAccessAuthorizationResult changedContextResult =
            await authorization.AuthorizeAsync(
                user.Id,
                organizationB.Id,
                processB.Id);

        Assert.Equal(ProcessAccessAuthorizationResult.Allowed, sameTenantResult);
        Assert.Equal(ProcessAccessAuthorizationResult.Denied, crossTenantResult);
        Assert.Equal(crossTenantResult, missingResult);
        Assert.Equal(ProcessAccessAuthorizationResult.Allowed, changedContextResult);
    }

    private static ProcessAccessAuthorization CreateAuthorization(
        EnmaDbContext dbContext)
    {
        return new ProcessAccessAuthorization(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(dbContext)),
            new ProcessOrganizationOwnershipLookup(dbContext));
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
}
