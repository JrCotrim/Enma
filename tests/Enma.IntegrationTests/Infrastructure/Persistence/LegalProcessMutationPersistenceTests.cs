using Enma.Application.Processes;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalProcessMutationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private readonly Dictionary<Guid, (Guid UserId, Guid MembershipId)> _actors = [];

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        13,
        19,
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
    public async Task UpdateTitleAsync_WithMatchingTenant_NormalizesTitleAndPreservesImmutableFields()
    {
        Organization organization = CreateOrganization(
            "Mutation Organization",
            "mutation-organization");
        var client = new Client(organization.Id, "Mutation Client", CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            "Initial title",
            CreatedAt);
        await SeedAsync(organization, client, legalProcess);

        LegalProcessMutationPersistenceResult result = await UpdateTitleAsync(
                CreatePersistence(),
                legalProcess.Id,
                organization.Id,
                "  Novo título  ");

        Assert.Equal(LegalProcessMutationPersistenceResult.Updated, result);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        LegalProcess persistedProcess = await verificationContext.LegalProcesses
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == legalProcess.Id);

        Assert.Equal("Novo título", persistedProcess.Title);
        Assert.Equal(organization.Id, persistedProcess.OrganizationId);
        Assert.Equal(client.Id, persistedProcess.ClientId);
        Assert.Equal(CreatedAt, persistedProcess.CreatedAt);
    }

    [Fact]
    public async Task UpdateTitleAsync_WithMissingAndCrossTenantProcess_ReturnsSameResultWithoutMutation()
    {
        Organization organizationA = CreateOrganization(
            "Mutation Organization A",
            "mutation-organization-a");
        Organization organizationB = CreateOrganization(
            "Mutation Organization B",
            "mutation-organization-b");
        var clientB = new Client(organizationB.Id, "Mutation Client B", CreatedAt);
        var processB = new LegalProcess(
            organizationB.Id,
            clientB.Id,
            "Organization B title",
            CreatedAt);
        await SeedAsync(organizationA, organizationB, clientB, processB);
        LegalProcessMutationPersistence persistence = CreatePersistence();

        LegalProcessMutationPersistenceResult missingResult =
            await UpdateTitleAsync(
                persistence,
                Guid.NewGuid(),
                organizationA.Id,
                "Missing update");
        LegalProcessMutationPersistenceResult crossTenantResult =
            await UpdateTitleAsync(
                persistence,
                processB.Id,
                organizationA.Id,
                "Cross-tenant update");

        Assert.Equal(LegalProcessMutationPersistenceResult.NotFound, missingResult);
        Assert.Equal(missingResult, crossTenantResult);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        LegalProcess persistedProcessB = await verificationContext.LegalProcesses
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == processB.Id);

        Assert.Equal("Organization B title", persistedProcessB.Title);
        Assert.Equal(organizationB.Id, persistedProcessB.OrganizationId);
        Assert.Equal(clientB.Id, persistedProcessB.ClientId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateTitleAsync_WithBlankTitle_RollsBackWithoutPersisting(
        string title)
    {
        (Organization organization, Client client, LegalProcess legalProcess) =
            await SeedProcessAsync();

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => UpdateTitleAsync(
                CreatePersistence(),
                legalProcess.Id,
                organization.Id,
                title));

        Assert.Equal("title", exception.ParamName);
        Assert.Contains(LegalProcessErrors.TitleRequired, exception.Message);
        await AssertProcessUnchangedAsync(organization, client, legalProcess);
    }

    [Fact]
    public async Task UpdateTitleAsync_WithTitleBeyondMaximum_RollsBackWithoutPersisting()
    {
        (Organization organization, Client client, LegalProcess legalProcess) =
            await SeedProcessAsync();

        ArgumentOutOfRangeException exception =
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                UpdateTitleAsync(
                    CreatePersistence(),
                    legalProcess.Id,
                    organization.Id,
                    new string('a', 151)));

        Assert.Equal("title", exception.ParamName);
        Assert.Contains(LegalProcessErrors.TitleTooLong, exception.Message);
        await AssertProcessUnchangedAsync(organization, client, legalProcess);
    }

    [Fact]
    public async Task UpdateTitleAsync_WithTitleAtMaximum_PersistsCompleteTitle()
    {
        (Organization organization, _, LegalProcess legalProcess) =
            await SeedProcessAsync();
        string title = new('a', 150);

        LegalProcessMutationPersistenceResult result = await UpdateTitleAsync(
            CreatePersistence(),
            legalProcess.Id,
            organization.Id,
            title);

        Assert.Equal(LegalProcessMutationPersistenceResult.Updated, result);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        string persistedTitle = await verificationContext.LegalProcesses
            .Where(candidate => candidate.Id == legalProcess.Id)
            .Select(candidate => candidate.Title)
            .SingleAsync();

        Assert.Equal(title, persistedTitle);
    }

    private LegalProcessMutationPersistence CreatePersistence()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;

        return new LegalProcessMutationPersistence(options, TimeProvider.System);
    }

    private Task<LegalProcessMutationPersistenceResult> UpdateTitleAsync(
        LegalProcessMutationPersistence persistence,
        Guid processId,
        Guid organizationId,
        string title,
        CancellationToken cancellationToken = default)
    {
        (Guid userId, Guid membershipId) = _actors[organizationId];
        return persistence.UpdateTitleAsync(
            new LegalProcessMutationPersistenceRequest(
                userId,
                organizationId,
                membershipId,
                processId),
            state =>
            {
                state.LegalProcess.ChangeTitle(title);
                return LegalProcessMutationDecision.Persist;
            },
            cancellationToken);
    }

    private async Task<(Organization, Client, LegalProcess)> SeedProcessAsync()
    {
        Organization organization = CreateOrganization(
            "Validation Organization",
            "validation-organization");
        var client = new Client(organization.Id, "Validation Client", CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            "Initial title",
            CreatedAt);
        await SeedAsync(organization, client, legalProcess);

        return (organization, client, legalProcess);
    }

    private async Task AssertProcessUnchangedAsync(
        Organization organization,
        Client client,
        LegalProcess legalProcess)
    {
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        LegalProcess persistedProcess = await verificationContext.LegalProcesses
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == legalProcess.Id);

        Assert.Equal("Initial title", persistedProcess.Title);
        Assert.Equal(organization.Id, persistedProcess.OrganizationId);
        Assert.Equal(client.Id, persistedProcess.ClientId);
        Assert.Equal(CreatedAt, persistedProcess.CreatedAt);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        foreach (Organization organization in entities.OfType<Organization>())
        {
            var user = new User(
                "Process audit actor",
                $"process-{organization.Id:N}@example.test",
                CreatedAt);
            var membership = new OrganizationMembership(
                organization.Id,
                user.Id,
                OrganizationRole.Owner,
                CreatedAt);
            dbContext.AddRange(user, membership);
            _actors[organization.Id] = (user.Id, membership.Id);
        }

        await dbContext.SaveChangesAsync();
    }

    private static Organization CreateOrganization(string name, string slug)
    {
        return new Organization(name, slug, CreatedAt);
    }
}
