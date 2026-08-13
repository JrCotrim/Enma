using Enma.Application.Authorization;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;

namespace Enma.IntegrationTests.Application.Authorization;

[Collection(PostgreSqlCollection.Name)]
public sealed class DeadlineAccessAuthorizationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        13,
        18,
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
    public async Task AuthorizeAsync_WithCrossTenantAndMissingDeadlines_EnforcesContextualOwnership()
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
        var deadlineA = new LegalDeadline(
            organizationA.Id,
            processA.Id,
            "Deadline A",
            new DateOnly(2026, 9, 1),
            CreatedAt);
        var deadlineB = new LegalDeadline(
            organizationB.Id,
            processB.Id,
            "Deadline B",
            new DateOnly(2026, 9, 2),
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
            processB,
            deadlineA,
            deadlineB);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        DeadlineAccessAuthorization authorization = CreateAuthorization(dbContext);

        DeadlineAccessAuthorizationResult sameTenant =
            await authorization.AuthorizeAsync(
                user.Id,
                organizationA.Id,
                deadlineA.Id);
        DeadlineAccessAuthorizationResult crossTenant =
            await authorization.AuthorizeAsync(
                user.Id,
                organizationA.Id,
                deadlineB.Id);
        DeadlineAccessAuthorizationResult missing =
            await authorization.AuthorizeAsync(
                user.Id,
                organizationA.Id,
                Guid.NewGuid());
        DeadlineAccessAuthorizationResult changedContext =
            await authorization.AuthorizeAsync(
                user.Id,
                organizationB.Id,
                deadlineB.Id);

        Assert.Equal(DeadlineAccessAuthorizationResult.Allowed, sameTenant);
        Assert.Equal(DeadlineAccessAuthorizationResult.Denied, crossTenant);
        Assert.Equal(crossTenant, missing);
        Assert.Equal(DeadlineAccessAuthorizationResult.Allowed, changedContext);
    }

    private static DeadlineAccessAuthorization CreateAuthorization(
        EnmaDbContext dbContext)
    {
        return new DeadlineAccessAuthorization(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(dbContext)),
            new DeadlineOrganizationOwnershipLookup(dbContext));
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
