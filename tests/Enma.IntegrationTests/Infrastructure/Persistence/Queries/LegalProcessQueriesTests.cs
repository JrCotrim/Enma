using System.Data.Common;
using Enma.Application.Processes;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Queries;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalProcessQueriesTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        13,
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
    public async Task ActiveClientLookup_WithTenantAndActivityMatrix_BindsAllPredicates()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "organization-b");
        var activeClientA = new Client(
            organizationA.Id,
            "Active Client A",
            CreatedAt);
        var inactiveClientA = new Client(
            organizationA.Id,
            "Inactive Client A",
            CreatedAt);
        inactiveClientA.Deactivate();
        var activeClientB = new Client(
            organizationB.Id,
            "Active Client B",
            CreatedAt);
        await SeedAsync(
            organizationA,
            organizationB,
            activeClientA,
            inactiveClientA,
            activeClientB);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var lookup = new ActiveClientInOrganizationLookup(dbContext);

        bool activeSameTenant = await lookup.ExistsAsync(
            activeClientA.Id,
            organizationA.Id);
        bool inactiveSameTenant = await lookup.ExistsAsync(
            inactiveClientA.Id,
            organizationA.Id);
        bool activeCrossTenant = await lookup.ExistsAsync(
            activeClientB.Id,
            organizationA.Id);
        bool missing = await lookup.ExistsAsync(
            Guid.NewGuid(),
            organizationA.Id);

        Assert.True(activeSameTenant);
        Assert.False(inactiveSameTenant);
        Assert.False(activeCrossTenant);
        Assert.False(missing);
    }

    [Fact]
    public async Task FindAsync_WithTenantBoundaryAndInactiveClient_ProjectsApprovedFields()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "organization-b");
        var clientA = new Client(organizationA.Id, "Client A", CreatedAt);
        clientA.Deactivate();
        var clientB = new Client(organizationB.Id, "Client B", CreatedAt);
        var processA = new LegalProcess(
            organizationA.Id,
            clientA.Id,
            "Process A",
            CreatedAt.AddMinutes(1));
        var processB = new LegalProcess(
            organizationB.Id,
            clientB.Id,
            "Process B",
            CreatedAt.AddMinutes(2));
        await SeedAsync(
            organizationA,
            organizationB,
            clientA,
            clientB,
            processA,
            processB);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new LegalProcessReadQueries(dbContext);

        LegalProcessReadModel? sameTenant = await queries.FindAsync(
            processA.Id,
            organizationA.Id);
        LegalProcessReadModel? crossTenant = await queries.FindAsync(
            processB.Id,
            organizationA.Id);
        LegalProcessReadModel? changedContext = await queries.FindAsync(
            processB.Id,
            organizationB.Id);

        Assert.Equal(
            new LegalProcessReadModel(
                processA.Id,
                processA.Title,
                clientA.Id,
                clientA.Name,
                processA.CreatedAt),
            sameTenant);
        Assert.Null(crossTenant);
        Assert.Equal(processB.Id, changedContext?.Id);
        Assert.Equal(clientB.Name, changedContext?.ClientName);
    }

    [Fact]
    public async Task ListAsync_WithTenantPaginationAndInactiveClient_UsesOneBoundedOrderedQuery()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "organization-b");
        var clientA = new Client(organizationA.Id, "Client A", CreatedAt);
        clientA.Deactivate();
        var clientB = new Client(organizationB.Id, "Client B", CreatedAt);
        var zetaA = new LegalProcess(
            organizationA.Id,
            clientA.Id,
            "Zeta",
            CreatedAt);
        var alphaA1 = new LegalProcess(
            organizationA.Id,
            clientA.Id,
            "Alpha",
            CreatedAt.AddMinutes(1));
        var alphaA2 = new LegalProcess(
            organizationA.Id,
            clientA.Id,
            "Alpha",
            CreatedAt.AddMinutes(2));
        var crossTenant = new LegalProcess(
            organizationB.Id,
            clientB.Id,
            "Aardvark",
            CreatedAt);
        await SeedAsync(
            organizationA,
            organizationB,
            clientA,
            clientB,
            zetaA,
            alphaA1,
            alphaA2,
            crossTenant);
        var interceptor = new ReaderCommandInterceptor();
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;
        await using var dbContext = new EnmaDbContext(options);
        var queries = new LegalProcessReadQueries(dbContext);

        IReadOnlyList<LegalProcessReadModel> firstPage = await queries.ListAsync(
            organizationA.Id,
            1,
            2);

        LegalProcess[] expectedFirstPage = new[] { alphaA1, alphaA2 }
            .OrderBy(legalProcess => legalProcess.Id)
            .ToArray();
        Assert.Equal(
            expectedFirstPage.Select(legalProcess => legalProcess.Id),
            firstPage.Select(legalProcess => legalProcess.Id));
        Assert.All(firstPage, item => Assert.Equal(clientA.Name, item.ClientName));
        Assert.DoesNotContain(firstPage, item => item.Id == crossTenant.Id);
        Assert.Equal(1, interceptor.ReaderCommandCount);
        Assert.Contains("INNER JOIN", interceptor.LastCommandText);
        Assert.Contains("ORDER BY", interceptor.LastCommandText);
        Assert.Contains("LIMIT", interceptor.LastCommandText);
        Assert.Contains("OFFSET", interceptor.LastCommandText);

        IReadOnlyList<LegalProcessReadModel> secondPage = await queries.ListAsync(
            organizationA.Id,
            2,
            2);

        LegalProcessReadModel item = Assert.Single(secondPage);
        Assert.Equal(zetaA.Id, item.Id);
        Assert.Equal(clientA.Name, item.ClientName);
        Assert.DoesNotContain("is_active", interceptor.LastCommandText);
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

    private sealed class ReaderCommandInterceptor : DbCommandInterceptor
    {
        public int ReaderCommandCount { get; private set; }

        public string LastCommandText { get; private set; } = string.Empty;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommandCount++;
            LastCommandText = command.CommandText;
            return ValueTask.FromResult(result);
        }
    }
}
