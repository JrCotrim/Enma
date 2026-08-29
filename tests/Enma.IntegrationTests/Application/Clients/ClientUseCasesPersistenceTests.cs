using Enma.Application.Authorization;
using Enma.Application.Clients;
using Enma.Application.Clients.Create;
using Enma.Application.Clients.Deactivate;
using Enma.Application.Clients.GetById;
using Enma.Application.Clients.List;
using Enma.Application.Clients.Reactivate;
using Enma.Application.Clients.Update;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Application.Clients;

[Collection(PostgreSqlCollection.Name)]
public sealed class ClientUseCasesPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        12,
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
    public async Task CreateAsync_WithRoleMatrixAndLiveChanges_UsesContextualCurrentRole()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "organization-b");
        User owner = CreateUser("Owner User", "owner@example.test");
        User administrator = CreateUser(
            "Administrator User",
            "administrator@example.test");
        User contextualUser = CreateUser(
            "Contextual User",
            "contextual@example.test");
        var ownerMembership = new OrganizationMembership(
            organizationA.Id,
            owner.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var administratorMembership = new OrganizationMembership(
            organizationA.Id,
            administrator.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        var memberMembershipA = new OrganizationMembership(
            organizationA.Id,
            contextualUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        var ownerMembershipB = new OrganizationMembership(
            organizationB.Id,
            contextualUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        await SeedAsync(
            organizationA,
            organizationB,
            owner,
            administrator,
            contextualUser,
            ownerMembership,
            administratorMembership,
            memberMembershipA,
            ownerMembershipB);

        await using EnmaDbContext operationContext = fixture.CreateDbContext();
        CreateClientUseCase useCase = CreateCreateUseCase(operationContext);

        CreateClientResult ownerResult = await useCase.ExecuteAsync(
            owner.Id,
            organizationA.Id,
            "Owner Client");
        CreateClientResult administratorResult = await useCase.ExecuteAsync(
            administrator.Id,
            organizationA.Id,
            "Administrator Client");
        CreateClientResult contextualMemberResult = await useCase.ExecuteAsync(
            contextualUser.Id,
            organizationA.Id,
            "Denied Member Client");
        CreateClientResult contextualOwnerResult = await useCase.ExecuteAsync(
            contextualUser.Id,
            organizationB.Id,
            "Organization B Client");

        Assert.Equal(CreateClientResultStatus.Succeeded, ownerResult.Status);
        Assert.Equal(
            CreateClientResultStatus.Succeeded,
            administratorResult.Status);
        Assert.Equal(
            CreateClientResultStatus.AccessDenied,
            contextualMemberResult.Status);
        Assert.Equal(
            CreateClientResultStatus.Succeeded,
            contextualOwnerResult.Status);

        await ChangeRoleAsync(
            contextualUser.Id,
            organizationA.Id,
            OrganizationRole.Administrator);

        CreateClientResult promotedResult = await useCase.ExecuteAsync(
            contextualUser.Id,
            organizationA.Id,
            "Promoted Client");

        Assert.Equal(CreateClientResultStatus.Succeeded, promotedResult.Status);

        await ChangeRoleAsync(
            contextualUser.Id,
            organizationA.Id,
            OrganizationRole.Member);

        CreateClientResult demotedResult = await useCase.ExecuteAsync(
            contextualUser.Id,
            organizationA.Id,
            "Denied Demoted Client");

        Assert.Equal(CreateClientResultStatus.AccessDenied, demotedResult.Status);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        var persistedClients = await verificationContext.Clients
            .AsNoTracking()
            .OrderBy(client => client.Name)
            .ToArrayAsync();

        Assert.Equal(4, persistedClients.Length);
        Assert.Contains(
            persistedClients,
            client => client.Name == "Owner Client" &&
                client.OrganizationId == organizationA.Id);
        Assert.Contains(
            persistedClients,
            client => client.Name == "Administrator Client" &&
                client.OrganizationId == organizationA.Id);
        Assert.Contains(
            persistedClients,
            client => client.Name == "Promoted Client" &&
                client.OrganizationId == organizationA.Id);
        Assert.Contains(
            persistedClients,
            client => client.Name == "Organization B Client" &&
                client.OrganizationId == organizationB.Id);
        Assert.DoesNotContain(
            persistedClients,
            client => client.Name.StartsWith("Denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadAsync_WithCrossTenantDualMembershipAndPagination_BindsContext()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "organization-b");
        User user = CreateUser("Member User", "member@example.test");
        var membershipA = new OrganizationMembership(
            organizationA.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt);
        var membershipB = new OrganizationMembership(
            organizationB.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt);
        var alphaClientA = new Client(
            organizationA.Id,
            "Alpha Client",
            CreatedAt);
        var betaClientA = new Client(
            organizationA.Id,
            "Beta Client",
            CreatedAt.AddMinutes(1));
        betaClientA.Deactivate();
        var gammaClientA = new Client(
            organizationA.Id,
            "Gamma Client",
            CreatedAt.AddMinutes(2));
        var clientB = new Client(
            organizationB.Id,
            "Aardvark Cross Tenant Client",
            CreatedAt);
        await SeedAsync(
            organizationA,
            organizationB,
            user,
            membershipA,
            membershipB,
            alphaClientA,
            betaClientA,
            gammaClientA,
            clientB);

        await using EnmaDbContext operationContext = fixture.CreateDbContext();
        GetClientUseCase getUseCase = CreateGetUseCase(operationContext);
        ListClientsUseCase listUseCase = CreateListUseCase(operationContext);

        GetClientResult sameTenantResult = await getUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            alphaClientA.Id);
        GetClientResult crossTenantResult = await getUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            clientB.Id);
        GetClientResult missingResult = await getUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            Guid.NewGuid());
        GetClientResult changedContextResult = await getUseCase.ExecuteAsync(
            user.Id,
            organizationB.Id,
            clientB.Id);

        Assert.Equal(GetClientResultStatus.Succeeded, sameTenantResult.Status);
        Assert.Equal(alphaClientA.Id, sameTenantResult.Client?.Id);
        Assert.Equal(GetClientResultStatus.NotFound, crossTenantResult.Status);
        Assert.Equal(missingResult.Status, crossTenantResult.Status);
        Assert.Null(crossTenantResult.Client);
        Assert.Null(missingResult.Client);
        Assert.Equal(GetClientResultStatus.Succeeded, changedContextResult.Status);
        Assert.Equal(clientB.Id, changedContextResult.Client?.Id);

        ListClientsResult firstPage = await listUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            1,
            2);
        ListClientsResult secondPage = await listUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            2,
            2);
        ListClientsResult allClientsA = await listUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            1,
            100);

        Assert.Equal(
            ["Alpha Client", "Beta Client"],
            firstPage.Items.Select(client => client.Name).ToArray());
        Assert.Equal(
            ["Gamma Client"],
            secondPage.Items.Select(client => client.Name).ToArray());
        Assert.Equal(3, allClientsA.Items.Count);
        Assert.Contains(
            allClientsA.Items,
            client => client.Id == betaClientA.Id && !client.IsActive);
        Assert.DoesNotContain(
            firstPage.Items.Concat(secondPage.Items),
            client => client.Id == clientB.Id);
    }

    [Fact]
    public async Task UpdateAsync_WithTenantAndLiveRoleMatrix_PreservesContextIsolation()
    {
        Organization organizationA = CreateOrganization(
            "Mutation Organization A",
            "mutation-organization-a");
        Organization organizationB = CreateOrganization(
            "Mutation Organization B",
            "mutation-organization-b");
        User owner = CreateUser("Update Owner", "update-owner@example.test");
        User administrator = CreateUser(
            "Update Administrator",
            "update-administrator@example.test");
        User contextualUser = CreateUser(
            "Update Contextual User",
            "update-contextual@example.test");
        User dualMember = CreateUser(
            "Update Dual Member",
            "update-dual@example.test");
        var ownerMembership = new OrganizationMembership(
            organizationA.Id,
            owner.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var administratorMembership = new OrganizationMembership(
            organizationA.Id,
            administrator.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        var contextualMembershipA = new OrganizationMembership(
            organizationA.Id,
            contextualUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        var contextualMembershipB = new OrganizationMembership(
            organizationB.Id,
            contextualUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var dualMembershipA = new OrganizationMembership(
            organizationA.Id,
            dualMember.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        var dualMembershipB = new OrganizationMembership(
            organizationB.Id,
            dualMember.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        var clientA = new Client(organizationA.Id, "Client A", CreatedAt);
        var clientB = new Client(organizationB.Id, "Client B", CreatedAt);
        await SeedAsync(
            organizationA,
            organizationB,
            owner,
            administrator,
            contextualUser,
            dualMember,
            ownerMembership,
            administratorMembership,
            contextualMembershipA,
            contextualMembershipB,
            dualMembershipA,
            dualMembershipB,
            clientA,
            clientB);

        await using EnmaDbContext operationContext = fixture.CreateDbContext();
        UpdateClientUseCase useCase = CreateUpdateUseCase(operationContext);

        UpdateClientResult ownerResult = await useCase.ExecuteAsync(
            owner.Id,
            organizationA.Id,
            clientA.Id,
            "Owner Updated");
        UpdateClientResult administratorResult = await useCase.ExecuteAsync(
            administrator.Id,
            organizationA.Id,
            clientA.Id,
            "Administrator Updated");
        UpdateClientResult crossTenantResult = await useCase.ExecuteAsync(
            owner.Id,
            organizationA.Id,
            clientB.Id,
            "Cross Tenant Attempt");
        UpdateClientResult dualWrongContextResult = await useCase.ExecuteAsync(
            dualMember.Id,
            organizationA.Id,
            clientB.Id,
            "Dual Wrong Context Attempt");
        UpdateClientResult contextualMemberResult = await useCase.ExecuteAsync(
            contextualUser.Id,
            organizationA.Id,
            clientA.Id,
            "Owner Elsewhere Attempt");

        Assert.Equal(UpdateClientResultStatus.Succeeded, ownerResult.Status);
        Assert.Equal(
            UpdateClientResultStatus.Succeeded,
            administratorResult.Status);
        Assert.Equal(UpdateClientResultStatus.NotFound, crossTenantResult.Status);
        Assert.Equal(
            UpdateClientResultStatus.NotFound,
            dualWrongContextResult.Status);
        Assert.Equal(
            UpdateClientResultStatus.AccessDenied,
            contextualMemberResult.Status);

        await using (EnmaDbContext isolationVerificationContext =
            fixture.CreateDbContext())
        {
            Client isolatedClientA = await isolationVerificationContext.Clients
                .AsNoTracking()
                .SingleAsync(client => client.Id == clientA.Id);
            Client isolatedClientB = await isolationVerificationContext.Clients
                .AsNoTracking()
                .SingleAsync(client => client.Id == clientB.Id);

            Assert.Equal("Administrator Updated", isolatedClientA.Name);
            Assert.Equal("Client B", isolatedClientB.Name);
        }

        UpdateClientResult dualCorrectContextResult = await useCase.ExecuteAsync(
            dualMember.Id,
            organizationB.Id,
            clientB.Id,
            "Dual Correct Context");

        Assert.Equal(
            UpdateClientResultStatus.Succeeded,
            dualCorrectContextResult.Status);

        await ChangeRoleAsync(
            contextualUser.Id,
            organizationA.Id,
            OrganizationRole.Administrator);

        UpdateClientResult promotedResult = await useCase.ExecuteAsync(
            contextualUser.Id,
            organizationA.Id,
            clientA.Id,
            "Live Role Updated");

        Assert.Equal(UpdateClientResultStatus.Succeeded, promotedResult.Status);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Client persistedClientA = await verificationContext.Clients
            .AsNoTracking()
            .SingleAsync(client => client.Id == clientA.Id);
        Client persistedClientB = await verificationContext.Clients
            .AsNoTracking()
            .SingleAsync(client => client.Id == clientB.Id);

        Assert.Equal("Live Role Updated", persistedClientA.Name);
        Assert.Equal("Dual Correct Context", persistedClientB.Name);
        Assert.Equal(organizationA.Id, persistedClientA.OrganizationId);
        Assert.Equal(organizationB.Id, persistedClientB.OrganizationId);
    }

    [Fact]
    public async Task DeactivateAsync_WithRoleTenantAndIdempotencyMatrix_PersistsSafely()
    {
        Organization organizationA = CreateOrganization(
            "Deactivate Organization A",
            "deactivate-organization-a");
        Organization organizationB = CreateOrganization(
            "Deactivate Organization B",
            "deactivate-organization-b");
        User owner = CreateUser(
            "Deactivate Owner",
            "deactivate-owner@example.test");
        User administrator = CreateUser(
            "Deactivate Administrator",
            "deactivate-administrator@example.test");
        User contextualUser = CreateUser(
            "Deactivate Contextual User",
            "deactivate-contextual@example.test");
        var ownerMembership = new OrganizationMembership(
            organizationA.Id,
            owner.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var administratorMembership = new OrganizationMembership(
            organizationA.Id,
            administrator.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        var contextualMembershipA = new OrganizationMembership(
            organizationA.Id,
            contextualUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        var contextualMembershipB = new OrganizationMembership(
            organizationB.Id,
            contextualUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var ownerClientA = new Client(
            organizationA.Id,
            "Owner Client A",
            CreatedAt);
        var administratorClientA = new Client(
            organizationA.Id,
            "Administrator Client A",
            CreatedAt);
        var clientB = new Client(organizationB.Id, "Client B", CreatedAt);
        await SeedAsync(
            organizationA,
            organizationB,
            owner,
            administrator,
            contextualUser,
            ownerMembership,
            administratorMembership,
            contextualMembershipA,
            contextualMembershipB,
            ownerClientA,
            administratorClientA,
            clientB);

        await using EnmaDbContext operationContext = fixture.CreateDbContext();
        DeactivateClientUseCase useCase = CreateDeactivateUseCase(operationContext);

        DeactivateClientResult ownerResult = await useCase.ExecuteAsync(
            owner.Id,
            organizationA.Id,
            ownerClientA.Id);
        DeactivateClientResult repeatedResult = await useCase.ExecuteAsync(
            owner.Id,
            organizationA.Id,
            ownerClientA.Id);
        DeactivateClientResult administratorResult = await useCase.ExecuteAsync(
            administrator.Id,
            organizationA.Id,
            administratorClientA.Id);
        DeactivateClientResult crossTenantResult = await useCase.ExecuteAsync(
            owner.Id,
            organizationA.Id,
            clientB.Id);
        DeactivateClientResult contextualMemberResult = await useCase.ExecuteAsync(
            contextualUser.Id,
            organizationA.Id,
            administratorClientA.Id);

        Assert.Equal(DeactivateClientResultStatus.Succeeded, ownerResult.Status);
        Assert.Equal(DeactivateClientResultStatus.Succeeded, repeatedResult.Status);
        Assert.Equal(
            DeactivateClientResultStatus.Succeeded,
            administratorResult.Status);
        Assert.Equal(
            DeactivateClientResultStatus.NotFound,
            crossTenantResult.Status);
        Assert.Equal(
            DeactivateClientResultStatus.AccessDenied,
            contextualMemberResult.Status);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.False((await verificationContext.Clients
            .AsNoTracking()
            .SingleAsync(client => client.Id == ownerClientA.Id)).IsActive);
        Assert.False((await verificationContext.Clients
            .AsNoTracking()
            .SingleAsync(client => client.Id == administratorClientA.Id)).IsActive);
        Assert.True((await verificationContext.Clients
            .AsNoTracking()
            .SingleAsync(client => client.Id == clientB.Id)).IsActive);
    }

    [Fact]
    public async Task ReactivateAsync_WithRoleTenantAndIdempotencyMatrix_PersistsSafely()
    {
        Organization organizationA = CreateOrganization(
            "Reactivate Organization A",
            "reactivate-organization-a");
        Organization organizationB = CreateOrganization(
            "Reactivate Organization B",
            "reactivate-organization-b");
        User owner = CreateUser(
            "Reactivate Owner",
            "reactivate-owner@example.test");
        User administrator = CreateUser(
            "Reactivate Administrator",
            "reactivate-administrator@example.test");
        User contextualUser = CreateUser(
            "Reactivate Contextual User",
            "reactivate-contextual@example.test");
        var ownerMembership = new OrganizationMembership(
            organizationA.Id,
            owner.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var administratorMembership = new OrganizationMembership(
            organizationA.Id,
            administrator.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        var contextualMembershipA = new OrganizationMembership(
            organizationA.Id,
            contextualUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        var contextualMembershipB = new OrganizationMembership(
            organizationB.Id,
            contextualUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var ownerClientA = new Client(
            organizationA.Id,
            "Owner Client A",
            CreatedAt);
        var administratorClientA = new Client(
            organizationA.Id,
            "Administrator Client A",
            CreatedAt);
        var clientB = new Client(organizationB.Id, "Client B", CreatedAt);
        ownerClientA.Deactivate();
        administratorClientA.Deactivate();
        clientB.Deactivate();
        await SeedAsync(
            organizationA,
            organizationB,
            owner,
            administrator,
            contextualUser,
            ownerMembership,
            administratorMembership,
            contextualMembershipA,
            contextualMembershipB,
            ownerClientA,
            administratorClientA,
            clientB);

        await using EnmaDbContext operationContext = fixture.CreateDbContext();
        ReactivateClientUseCase useCase = CreateReactivateUseCase(operationContext);

        ReactivateClientResult ownerResult = await useCase.ExecuteAsync(
            owner.Id,
            organizationA.Id,
            ownerClientA.Id);
        ReactivateClientResult repeatedResult = await useCase.ExecuteAsync(
            owner.Id,
            organizationA.Id,
            ownerClientA.Id);
        ReactivateClientResult administratorResult = await useCase.ExecuteAsync(
            administrator.Id,
            organizationA.Id,
            administratorClientA.Id);
        ReactivateClientResult crossTenantResult = await useCase.ExecuteAsync(
            owner.Id,
            organizationA.Id,
            clientB.Id);
        ReactivateClientResult contextualMemberResult = await useCase.ExecuteAsync(
            contextualUser.Id,
            organizationA.Id,
            administratorClientA.Id);

        Assert.Equal(ReactivateClientResultStatus.Succeeded, ownerResult.Status);
        Assert.Equal(ReactivateClientResultStatus.Succeeded, repeatedResult.Status);
        Assert.Equal(
            ReactivateClientResultStatus.Succeeded,
            administratorResult.Status);
        Assert.Equal(
            ReactivateClientResultStatus.NotFound,
            crossTenantResult.Status);
        Assert.Equal(
            ReactivateClientResultStatus.AccessDenied,
            contextualMemberResult.Status);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.True((await verificationContext.Clients
            .AsNoTracking()
            .SingleAsync(client => client.Id == ownerClientA.Id)).IsActive);
        Assert.True((await verificationContext.Clients
            .AsNoTracking()
            .SingleAsync(client => client.Id == administratorClientA.Id)).IsActive);
        Assert.False((await verificationContext.Clients
            .AsNoTracking()
            .SingleAsync(client => client.Id == clientB.Id)).IsActive);
    }

    [Fact]
    public async Task UpdateAsync_RoleDowngradedAfterInitialAuthorization_DeniesLive()
    {
        Organization organization = CreateOrganization(
            "Stale client organization",
            "stale-client-organization");
        User user = CreateUser("Stale client actor", "stale-client@example.test");
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var client = new Client(organization.Id, "Original client", CreatedAt);
        await SeedAsync(organization, user, membership, client);
        await using EnmaDbContext operationContext = fixture.CreateDbContext();
        var persistence = new BeforeClientMutationPersistence(
            CreateMutationPersistence(),
            () => ChangeRoleAsync(
                user.Id,
                organization.Id,
                OrganizationRole.Member));
        var useCase = new UpdateClientUseCase(
            CreateActionAuthorization(operationContext),
            persistence);

        UpdateClientResult result = await useCase.ExecuteAsync(
            user.Id,
            organization.Id,
            client.Id,
            "Must not persist");

        Assert.Same(UpdateClientResult.AccessDenied, result);
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.Equal(
            "Original client",
            await verificationContext.Clients
                .Where(candidate => candidate.Id == client.Id)
                .Select(candidate => candidate.Name)
                .SingleAsync());
        Assert.False(await verificationContext.AuditLogs.AnyAsync());
    }

    private CreateClientUseCase CreateCreateUseCase(EnmaDbContext dbContext)
    {
        var timeProvider = new FixedTimeProvider(CreatedAt.AddHours(1));
        return new CreateClientUseCase(
            CreateActionAuthorization(dbContext),
            new ClientCreationPersistence(
                CreateDbContextOptions(),
                timeProvider),
            timeProvider);
    }

    private static GetClientUseCase CreateGetUseCase(EnmaDbContext dbContext)
    {
        return new GetClientUseCase(
            CreateActionAuthorization(dbContext),
            new ClientReadQueries(dbContext));
    }

    private static ListClientsUseCase CreateListUseCase(EnmaDbContext dbContext)
    {
        return new ListClientsUseCase(
            CreateActionAuthorization(dbContext),
            new ClientReadQueries(dbContext));
    }

    private UpdateClientUseCase CreateUpdateUseCase(EnmaDbContext dbContext)
    {
        return new UpdateClientUseCase(
            CreateActionAuthorization(dbContext),
            CreateMutationPersistence());
    }

    private DeactivateClientUseCase CreateDeactivateUseCase(EnmaDbContext dbContext)
    {
        return new DeactivateClientUseCase(
            CreateActionAuthorization(dbContext),
            CreateMutationPersistence());
    }

    private ReactivateClientUseCase CreateReactivateUseCase(EnmaDbContext dbContext)
    {
        return new ReactivateClientUseCase(
            CreateActionAuthorization(dbContext),
            CreateMutationPersistence());
    }

    private ClientMutationPersistence CreateMutationPersistence()
    {
        return new ClientMutationPersistence(
            CreateDbContextOptions(),
            new FixedTimeProvider(CreatedAt.AddHours(2)));
    }

    private DbContextOptions<EnmaDbContext> CreateDbContextOptions()
    {
        return new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
    }

    private static ClientActionAuthorization CreateActionAuthorization(
        EnmaDbContext dbContext)
    {
        return new ClientActionAuthorization(
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed class BeforeClientMutationPersistence(
        IClientMutationPersistence inner,
        Func<Task> before) : IClientMutationPersistence
    {
        public async Task<ClientMutationPersistenceResult> UpdateNameAsync(
            ClientMutationPersistenceRequest request,
            Func<ClientMutationLockedState, ClientMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            await before();
            return await inner.UpdateNameAsync(request, decide, cancellationToken);
        }

        public Task<ClientMutationPersistenceResult> DeactivateAsync(
            ClientMutationPersistenceRequest request,
            Func<ClientMutationLockedState, ClientMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ClientMutationPersistenceResult> ReactivateAsync(
            ClientMutationPersistenceRequest request,
            Func<ClientMutationLockedState, ClientMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
