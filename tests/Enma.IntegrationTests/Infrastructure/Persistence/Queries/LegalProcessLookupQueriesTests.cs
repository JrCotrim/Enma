using System.Data.Common;
using Enma.Application.Processes.Lookup;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Queries;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalProcessLookupQueriesTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        14,
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
    public async Task SearchAsync_WithTitleClientTenantAndLiteralSearch_UsesOneBoundedProjectedQuery()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "process-lookup-organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "process-lookup-organization-b");
        var clientA = new Client(
            organizationA.Id,
            "Ordinary Client",
            CreatedAt);
        var matchingClientA = new Client(
            organizationA.Id,
            "CLIENT CONTEXT ALPHA",
            CreatedAt.AddMinutes(1));
        matchingClientA.Deactivate();
        var clientB = new Client(
            organizationB.Id,
            "Cross Tenant Client",
            CreatedAt.AddMinutes(2));
        var percentProcess = CreateProcess(
            organizationA,
            clientA,
            "Literal % Process",
            1);
        var underscoreProcess = CreateProcess(
            organizationA,
            clientA,
            "Literal _ Process",
            2);
        var backslashProcess = CreateProcess(
            organizationA,
            clientA,
            "Literal \\ Process",
            3);
        var uppercaseProcess = CreateProcess(
            organizationA,
            clientA,
            "PRAZO RECURSAL PRINCIPAL",
            4);
        var clientNameProcess = CreateProcess(
            organizationA,
            matchingClientA,
            "Unrelated Process Title",
            5);
        var crossTenantProcess = CreateProcess(
            organizationB,
            clientB,
            "Cross Tenant Only Process",
            6);
        await SeedAsync(
            organizationA,
            organizationB,
            clientA,
            matchingClientA,
            clientB,
            percentProcess,
            underscoreProcess,
            backslashProcess,
            uppercaseProcess,
            clientNameProcess,
            crossTenantProcess);
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateQueryContext(interceptor);
        var queries = new LegalProcessLookupQueries(dbContext);

        IReadOnlyList<LegalProcessLookupItem> percentResults =
            await queries.SearchAsync(organizationA.Id, "%", 1, 20);

        LegalProcessLookupItem percentItem = Assert.Single(percentResults);
        Assert.Equal(percentProcess.Id, percentItem.Id);
        Assert.Equal(percentProcess.Title, percentItem.Title);
        Assert.Equal(clientA.Name, percentItem.ClientName);
        Assert.Equal(1, interceptor.ReaderCommandCount);
        Assert.Contains("INNER JOIN", interceptor.LastCommandText);
        Assert.Contains("organization_id", interceptor.LastCommandText);
        Assert.Contains("ILIKE", interceptor.LastCommandText);
        Assert.Contains(" OR ", interceptor.LastCommandText);
        Assert.Contains("ESCAPE", interceptor.LastCommandText);
        Assert.Contains("@", interceptor.LastCommandText);
        Assert.Contains("ORDER BY", interceptor.LastCommandText);
        Assert.Contains("LIMIT", interceptor.LastCommandText);
        Assert.Contains("OFFSET", interceptor.LastCommandText);
        Assert.DoesNotContain("is_active", interceptor.LastCommandText);
        Assert.DoesNotContain("created_at", interceptor.LastCommandText);
        Assert.DoesNotContain("organizations", interceptor.LastCommandText);
        Assert.DoesNotContain("COUNT(", interceptor.LastCommandText);
        Assert.DoesNotContain(percentProcess.Title, interceptor.LastCommandText);

        IReadOnlyList<LegalProcessLookupItem> underscoreResults =
            await queries.SearchAsync(organizationA.Id, "_", 1, 20);
        IReadOnlyList<LegalProcessLookupItem> backslashResults =
            await queries.SearchAsync(organizationA.Id, "\\", 1, 20);
        IReadOnlyList<LegalProcessLookupItem> titleResults =
            await queries.SearchAsync(organizationA.Id, "recursal", 1, 20);
        IReadOnlyList<LegalProcessLookupItem> clientNameResults =
            await queries.SearchAsync(organizationA.Id, "client context", 1, 20);
        IReadOnlyList<LegalProcessLookupItem> crossTenantResults =
            await queries.SearchAsync(
                organizationA.Id,
                crossTenantProcess.Title,
                1,
                20);

        Assert.Equal(underscoreProcess.Id, Assert.Single(underscoreResults).Id);
        Assert.Equal(backslashProcess.Id, Assert.Single(backslashResults).Id);
        Assert.Equal(uppercaseProcess.Id, Assert.Single(titleResults).Id);
        LegalProcessLookupItem clientNameItem = Assert.Single(clientNameResults);
        Assert.Equal(clientNameProcess.Id, clientNameItem.Id);
        Assert.Equal(matchingClientA.Name, clientNameItem.ClientName);
        Assert.Empty(crossTenantResults);
        Assert.Equal(6, interceptor.ReaderCommandCount);
    }

    [Fact]
    public async Task SearchAsync_WithMoreThanOnePage_ReturnsSentinelAndDiscoversLaterProcess()
    {
        Organization organization = CreateOrganization(
            "Paged Organization",
            "process-lookup-paged-organization");
        var client = new Client(
            organization.Id,
            "Paged Client Context",
            CreatedAt);
        LegalProcess[] legalProcesses = Enumerable.Range(1, 22)
            .Select(index => CreateProcess(
                organization,
                client,
                $"Process {index:D2}",
                index))
            .ToArray();
        var sameTitleFirst = CreateProcess(
            organization,
            client,
            "Same Title",
            30);
        var sameTitleSecond = CreateProcess(
            organization,
            client,
            "Same Title",
            31);
        await SeedAsync(
            new object[] { organization, client }
                .Concat(legalProcesses)
                .Append(sameTitleFirst)
                .Append(sameTitleSecond)
                .ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateQueryContext(interceptor);
        var queries = new LegalProcessLookupQueries(dbContext);

        IReadOnlyList<LegalProcessLookupItem> firstPageWithSentinel =
            await queries.SearchAsync(organization.Id, null, 1, 20);
        IReadOnlyList<LegalProcessLookupItem> secondPage =
            await queries.SearchAsync(organization.Id, null, 2, 20);
        IReadOnlyList<LegalProcessLookupItem> outsideFirstPageTitleSearch =
            await queries.SearchAsync(organization.Id, "Process 22", 1, 20);
        IReadOnlyList<LegalProcessLookupItem> outsideFirstPageClientSearch =
            await queries.SearchAsync(organization.Id, "paged client", 2, 20);
        IReadOnlyList<LegalProcessLookupItem> sameTitleResults =
            await queries.SearchAsync(organization.Id, "Same Title", 1, 20);

        Assert.Equal(21, firstPageWithSentinel.Count);
        Assert.Equal(
            new[] { legalProcesses[20].Id, legalProcesses[21].Id }
                .Concat(
                    new[] { sameTitleFirst.Id, sameTitleSecond.Id }
                        .OrderBy(id => id)),
            secondPage.Select(item => item.Id));
        Assert.Equal(
            legalProcesses[21].Id,
            Assert.Single(outsideFirstPageTitleSearch).Id);
        Assert.Contains(
            outsideFirstPageClientSearch,
            item => item.Id == legalProcesses[21].Id);
        Assert.Equal(
            new[] { sameTitleFirst.Id, sameTitleSecond.Id }.OrderBy(id => id),
            sameTitleResults.Select(item => item.Id));
        Assert.Equal(5, interceptor.ReaderCommandCount);
    }

    private EnmaDbContext CreateQueryContext(ReaderCommandInterceptor interceptor)
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;

        return new EnmaDbContext(options);
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

    private static LegalProcess CreateProcess(
        Organization organization,
        Client client,
        string title,
        int createdMinutesAgo)
    {
        return new LegalProcess(
            organization.Id,
            client.Id,
            title,
            CreatedAt.AddMinutes(-createdMinutesAgo));
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
