using Enma.Application.Authorization;
using Enma.Application.Clients.Create;
using Enma.Application.Clients.GetById;
using Enma.Application.Clients.List;
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

    private static CreateClientUseCase CreateCreateUseCase(EnmaDbContext dbContext)
    {
        return new CreateClientUseCase(
            CreateActionAuthorization(dbContext),
            new ClientCreationPersistence(dbContext),
            new FixedTimeProvider(CreatedAt.AddHours(1)));
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
}
