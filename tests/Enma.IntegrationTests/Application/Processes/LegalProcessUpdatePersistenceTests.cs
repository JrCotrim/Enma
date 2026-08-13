using Enma.Application.Authorization;
using Enma.Application.Processes.Update;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Application.Processes;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalProcessUpdatePersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        13,
        21,
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
    public async Task ExecuteAsync_WithLiveRolesDualMembershipAndInactiveClient_PreservesMutationBoundaries()
    {
        Organization organizationA = CreateOrganization(
            "Update Organization A",
            "update-organization-a");
        Organization organizationB = CreateOrganization(
            "Update Organization B",
            "update-organization-b");
        User user = new("Update User", "update-user@example.test", CreatedAt);
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
        var clientA = new Client(organizationA.Id, "Update Client A", CreatedAt);
        var clientB = new Client(organizationB.Id, "Update Client B", CreatedAt);
        var processA = new LegalProcess(
            organizationA.Id,
            clientA.Id,
            "Initial A",
            CreatedAt);
        var processB = new LegalProcess(
            organizationB.Id,
            clientB.Id,
            "Initial B",
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
        await DeactivateClientAsync(clientB.Id);

        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        UpdateLegalProcessUseCase useCase = CreateUseCase(authorizationContext);

        UpdateLegalProcessResult memberResult = await useCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            processA.Id,
            "Denied member title");

        await ChangeRoleAsync(
            user.Id,
            organizationA.Id,
            OrganizationRole.Administrator);

        UpdateLegalProcessResult administratorResult = await useCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            processA.Id,
            "  Novo título A  ");
        UpdateLegalProcessResult crossTenantResult = await useCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            processB.Id,
            "Cross-tenant title");
        UpdateLegalProcessResult missingResult = await useCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            Guid.NewGuid(),
            "Missing title");

        await ChangeRoleAsync(
            user.Id,
            organizationA.Id,
            OrganizationRole.Member);

        UpdateLegalProcessResult demotedResult = await useCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            processA.Id,
            "Denied demoted title");
        UpdateLegalProcessResult ownerWithInactiveClientResult =
            await useCase.ExecuteAsync(
                user.Id,
                organizationB.Id,
                processB.Id,
                "  Novo título B  ");

        Assert.Equal(UpdateLegalProcessResultStatus.AccessDenied, memberResult.Status);
        Assert.Equal(
            UpdateLegalProcessResultStatus.Updated,
            administratorResult.Status);
        Assert.Same(UpdateLegalProcessResult.NotFound, crossTenantResult);
        Assert.Same(crossTenantResult, missingResult);
        Assert.Equal(UpdateLegalProcessResultStatus.AccessDenied, demotedResult.Status);
        Assert.Equal(
            UpdateLegalProcessResultStatus.Updated,
            ownerWithInactiveClientResult.Status);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        LegalProcess persistedProcessA = await verificationContext.LegalProcesses
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == processA.Id);
        LegalProcess persistedProcessB = await verificationContext.LegalProcesses
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == processB.Id);
        Client persistedClientB = await verificationContext.Clients
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == clientB.Id);

        Assert.Equal("Novo título A", persistedProcessA.Title);
        Assert.Equal(organizationA.Id, persistedProcessA.OrganizationId);
        Assert.Equal(clientA.Id, persistedProcessA.ClientId);
        Assert.Equal(CreatedAt, persistedProcessA.CreatedAt);
        Assert.Equal("Novo título B", persistedProcessB.Title);
        Assert.Equal(organizationB.Id, persistedProcessB.OrganizationId);
        Assert.Equal(clientB.Id, persistedProcessB.ClientId);
        Assert.Equal(CreatedAt, persistedProcessB.CreatedAt);
        Assert.False(persistedClientB.IsActive);
    }

    private UpdateLegalProcessUseCase CreateUseCase(EnmaDbContext dbContext)
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;

        return new UpdateLegalProcessUseCase(
            new ProcessActionAuthorization(
                new OrganizationAccessAuthorization(
                    new OrganizationAccessLookup(dbContext))),
            new LegalProcessMutationPersistence(options));
    }

    private async Task ChangeRoleAsync(
        Guid userId,
        Guid organizationId,
        OrganizationRole role)
    {
        await using EnmaDbContext mutationContext = fixture.CreateDbContext();
        OrganizationMembership membership = await mutationContext
            .OrganizationMemberships
            .SingleAsync(candidate =>
                candidate.UserId == userId &&
                candidate.OrganizationId == organizationId);
        membership.ChangeRole(role);
        await mutationContext.SaveChangesAsync();
    }

    private async Task DeactivateClientAsync(Guid clientId)
    {
        await using EnmaDbContext mutationContext = fixture.CreateDbContext();
        Client client = await mutationContext.Clients.SingleAsync(
            candidate => candidate.Id == clientId);
        client.Deactivate();
        await mutationContext.SaveChangesAsync();
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
