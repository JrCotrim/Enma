using Enma.Application.Authorization;
using Enma.Application.Processes.Create;
using Enma.Application.Processes.GetById;
using Enma.Application.Processes.List;
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
public sealed class LegalProcessUseCasesPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        13,
        17,
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
    public async Task CreateGetAndList_WithLiveContextualRolesAndClientDeactivation_PreserveBoundaries()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "organization-b");
        User user = new("Contextual User", "contextual@example.test", CreatedAt);
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
        await SeedAsync(
            organizationA,
            organizationB,
            user,
            membershipA,
            membershipB,
            clientA,
            clientB);

        await using EnmaDbContext operationContext = fixture.CreateDbContext();
        CreateLegalProcessUseCase createUseCase = CreateCreateUseCase(
            operationContext);
        GetLegalProcessUseCase getUseCase = CreateGetUseCase(operationContext);
        ListLegalProcessesUseCase listUseCase = CreateListUseCase(
            operationContext);

        CreateLegalProcessResult memberCreateA = await createUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            clientA.Id,
            "Denied Member Process");
        CreateLegalProcessResult ownerCreateB = await createUseCase.ExecuteAsync(
            user.Id,
            organizationB.Id,
            clientB.Id,
            "Organization B Process");

        Assert.Equal(
            CreateLegalProcessResultStatus.AccessDenied,
            memberCreateA.Status);
        Assert.Equal(
            CreateLegalProcessResultStatus.Succeeded,
            ownerCreateB.Status);

        await ChangeRoleAsync(
            user.Id,
            organizationA.Id,
            OrganizationRole.Administrator);

        CreateLegalProcessResult administratorCreateA =
            await createUseCase.ExecuteAsync(
                user.Id,
                organizationA.Id,
                clientA.Id,
                "Organization A Process");

        Assert.Equal(
            CreateLegalProcessResultStatus.Succeeded,
            administratorCreateA.Status);

        await ChangeRoleAsync(
            user.Id,
            organizationA.Id,
            OrganizationRole.Member);

        CreateLegalProcessResult demotedCreateA = await createUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            clientA.Id,
            "Denied Demoted Process");

        Assert.Equal(
            CreateLegalProcessResultStatus.AccessDenied,
            demotedCreateA.Status);

        Guid processAId = AssertProcessId(administratorCreateA);
        Guid processBId = AssertProcessId(ownerCreateB);
        await using (EnmaDbContext verificationContext = fixture.CreateDbContext())
        {
            LegalProcess persistedProcessA = await verificationContext
                .LegalProcesses
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == processAId);
            LegalProcess persistedProcessB = await verificationContext
                .LegalProcesses
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == processBId);

            Assert.Equal(organizationA.Id, persistedProcessA.OrganizationId);
            Assert.Equal(clientA.Id, persistedProcessA.ClientId);
            Assert.Equal(organizationB.Id, persistedProcessB.OrganizationId);
            Assert.Equal(clientB.Id, persistedProcessB.ClientId);
        }

        GetLegalProcessResult crossTenantGet = await getUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            processBId);
        GetLegalProcessResult getA = await getUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            processAId);
        GetLegalProcessResult getB = await getUseCase.ExecuteAsync(
            user.Id,
            organizationB.Id,
            processBId);

        Assert.Equal(GetLegalProcessResultStatus.NotFound, crossTenantGet.Status);
        Assert.Equal(GetLegalProcessResultStatus.Succeeded, getA.Status);
        Assert.Equal(GetLegalProcessResultStatus.Succeeded, getB.Status);

        await ChangeRoleAsync(
            user.Id,
            organizationA.Id,
            OrganizationRole.Administrator);
        await DeactivateClientAsync(clientA.Id);

        CreateLegalProcessResult inactiveClientCreate =
            await createUseCase.ExecuteAsync(
                user.Id,
                organizationA.Id,
                clientA.Id,
                "Unavailable Client Process");
        GetLegalProcessResult inactiveClientGet = await getUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            processAId);
        ListLegalProcessesResult listA = await listUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id);
        ListLegalProcessesResult listB = await listUseCase.ExecuteAsync(
            user.Id,
            organizationB.Id);

        Assert.Equal(
            CreateLegalProcessResultStatus.RelatedClientUnavailable,
            inactiveClientCreate.Status);
        Assert.Equal(
            GetLegalProcessResultStatus.Succeeded,
            inactiveClientGet.Status);
        Assert.Equal(clientA.Name, inactiveClientGet.LegalProcess?.ClientName);
        Assert.Collection(
            listA.Items,
            item =>
            {
                Assert.Equal(processAId, item.Id);
                Assert.Equal(clientA.Name, item.ClientName);
            });
        Assert.Collection(
            listB.Items,
            item =>
            {
                Assert.Equal(processBId, item.Id);
                Assert.Equal(clientB.Name, item.ClientName);
            });
    }

    [Fact]
    public async Task CreateAsync_WithMissingInactiveAndCrossTenantClients_ReturnsSameResultWithoutPersistence()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "organization-b");
        User owner = new("Owner", "owner@example.test", CreatedAt);
        var membership = new OrganizationMembership(
            organizationA.Id,
            owner.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var inactiveClientA = new Client(
            organizationA.Id,
            "Inactive Client A",
            CreatedAt);
        inactiveClientA.Deactivate();
        var clientB = new Client(organizationB.Id, "Client B", CreatedAt);
        await SeedAsync(
            organizationA,
            organizationB,
            owner,
            membership,
            inactiveClientA,
            clientB);

        await using EnmaDbContext operationContext = fixture.CreateDbContext();
        CreateLegalProcessUseCase useCase = CreateCreateUseCase(operationContext);

        CreateLegalProcessResult missingResult = await useCase.ExecuteAsync(
            owner.Id,
            organizationA.Id,
            Guid.NewGuid(),
            "Missing Client Process");
        CreateLegalProcessResult inactiveResult = await useCase.ExecuteAsync(
            owner.Id,
            organizationA.Id,
            inactiveClientA.Id,
            "Inactive Client Process");
        CreateLegalProcessResult crossTenantResult = await useCase.ExecuteAsync(
            owner.Id,
            organizationA.Id,
            clientB.Id,
            "Cross-tenant Client Process");

        Assert.Same(CreateLegalProcessResult.RelatedClientUnavailable, missingResult);
        Assert.Same(missingResult, inactiveResult);
        Assert.Same(missingResult, crossTenantResult);
        Assert.False(await operationContext.LegalProcesses.AnyAsync());
    }

    private static CreateLegalProcessUseCase CreateCreateUseCase(
        EnmaDbContext dbContext)
    {
        return new CreateLegalProcessUseCase(
            CreateActionAuthorization(dbContext),
            new ActiveClientInOrganizationLookup(dbContext),
            new LegalProcessCreationPersistence(dbContext),
            new FixedTimeProvider(CreatedAt.AddHours(1)));
    }

    private static GetLegalProcessUseCase CreateGetUseCase(EnmaDbContext dbContext)
    {
        return new GetLegalProcessUseCase(
            CreateActionAuthorization(dbContext),
            new LegalProcessReadQueries(dbContext));
    }

    private static ListLegalProcessesUseCase CreateListUseCase(
        EnmaDbContext dbContext)
    {
        return new ListLegalProcessesUseCase(
            CreateActionAuthorization(dbContext),
            new LegalProcessReadQueries(dbContext));
    }

    private static ProcessActionAuthorization CreateActionAuthorization(
        EnmaDbContext dbContext)
    {
        return new ProcessActionAuthorization(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(dbContext)));
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

    private static Guid AssertProcessId(CreateLegalProcessResult result)
    {
        return Assert.IsType<Guid>(result.ProcessId);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}
