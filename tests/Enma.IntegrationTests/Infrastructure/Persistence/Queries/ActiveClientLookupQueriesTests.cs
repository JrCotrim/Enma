using System.Data.Common;
using Enma.Application.Clients.Lookup;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Queries;

[Collection(PostgreSqlCollection.Name)]
public sealed class ActiveClientLookupQueriesTests(
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
    public async Task SearchAsync_WithTenantActivityAndLiteralSearch_UsesOneBoundedParameterizedQuery()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "lookup-organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "lookup-organization-b");
        var percentClient = new Client(
            organizationA.Id,
            "Literal % Client",
            CreatedAt);
        var underscoreClient = new Client(
            organizationA.Id,
            "Literal _ Client",
            CreatedAt.AddMinutes(1));
        var backslashClient = new Client(
            organizationA.Id,
            "Literal \\ Client",
            CreatedAt.AddMinutes(2));
        var caseClient = new Client(
            organizationA.Id,
            "UPPERCASE CLIENT",
            CreatedAt.AddMinutes(3));
        var inactivePercentClient = new Client(
            organizationA.Id,
            "Inactive % Client",
            CreatedAt.AddMinutes(4));
        inactivePercentClient.Deactivate();
        var crossTenantPercentClient = new Client(
            organizationB.Id,
            "Cross Tenant % Client",
            CreatedAt.AddMinutes(5));
        await SeedAsync(
            organizationA,
            organizationB,
            percentClient,
            underscoreClient,
            backslashClient,
            caseClient,
            inactivePercentClient,
            crossTenantPercentClient);
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateQueryContext(interceptor);
        var queries = new ActiveClientLookupQueries(dbContext);

        IReadOnlyList<ActiveClientLookupItem> percentResults =
            await queries.SearchAsync(organizationA.Id, "%", 1, 20);

        ActiveClientLookupItem percentItem = Assert.Single(percentResults);
        Assert.Equal(percentClient.Id, percentItem.Id);
        Assert.Equal(percentClient.Name, percentItem.Name);
        Assert.Equal(1, interceptor.ReaderCommandCount);
        Assert.Contains("organization_id", interceptor.LastCommandText);
        Assert.Contains("is_active", interceptor.LastCommandText);
        Assert.Contains("ILIKE", interceptor.LastCommandText);
        Assert.Contains("ESCAPE", interceptor.LastCommandText);
        Assert.Contains("@", interceptor.LastCommandText);
        Assert.Contains("ORDER BY", interceptor.LastCommandText);
        Assert.Contains("LIMIT", interceptor.LastCommandText);
        Assert.Contains("OFFSET", interceptor.LastCommandText);
        Assert.DoesNotContain("created_at", interceptor.LastCommandText);
        Assert.DoesNotContain("Literal % Client", interceptor.LastCommandText);

        IReadOnlyList<ActiveClientLookupItem> underscoreResults =
            await queries.SearchAsync(organizationA.Id, "_", 1, 20);
        IReadOnlyList<ActiveClientLookupItem> backslashResults =
            await queries.SearchAsync(organizationA.Id, "\\", 1, 20);
        IReadOnlyList<ActiveClientLookupItem> caseResults =
            await queries.SearchAsync(organizationA.Id, "uppercase", 1, 20);
        IReadOnlyList<ActiveClientLookupItem> crossTenantResults =
            await queries.SearchAsync(
                organizationA.Id,
                crossTenantPercentClient.Name,
                1,
                20);

        Assert.Equal(underscoreClient.Id, Assert.Single(underscoreResults).Id);
        Assert.Equal(backslashClient.Id, Assert.Single(backslashResults).Id);
        Assert.Equal(caseClient.Id, Assert.Single(caseResults).Id);
        Assert.Empty(crossTenantResults);
        Assert.Equal(5, interceptor.ReaderCommandCount);
    }

    [Fact]
    public async Task SearchAsync_WithMoreThanOnePage_ReturnsExtraRowAndDeterministicSubsequentPage()
    {
        Organization organization = CreateOrganization(
            "Paged Organization",
            "lookup-paged-organization");
        Client[] clients = Enumerable.Range(1, 22)
            .Select(index => new Client(
                organization.Id,
                $"Client {index:D2}",
                CreatedAt.AddMinutes(index)))
            .ToArray();
        var sameNameFirst = new Client(
            organization.Id,
            "Same Name",
            CreatedAt.AddHours(1));
        var sameNameSecond = new Client(
            organization.Id,
            "Same Name",
            CreatedAt.AddHours(2));
        await SeedAsync(
            new object[] { organization }
                .Concat(clients)
                .Append(sameNameFirst)
                .Append(sameNameSecond)
                .ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateQueryContext(interceptor);
        var queries = new ActiveClientLookupQueries(dbContext);

        IReadOnlyList<ActiveClientLookupItem> firstPageWithSentinel =
            await queries.SearchAsync(organization.Id, null, 1, 20);
        IReadOnlyList<ActiveClientLookupItem> secondPageWithSentinel =
            await queries.SearchAsync(organization.Id, null, 2, 20);
        IReadOnlyList<ActiveClientLookupItem> outsideFirstPageSearch =
            await queries.SearchAsync(organization.Id, "Client 22", 1, 20);
        IReadOnlyList<ActiveClientLookupItem> sameNameResults =
            await queries.SearchAsync(organization.Id, "Same Name", 1, 20);

        Assert.Equal(21, firstPageWithSentinel.Count);
        Assert.Equal(
            new[]
            {
                clients[20].Id,
                clients[21].Id,
                sameNameFirst.Id,
                sameNameSecond.Id
            }
                .OrderBy(id => id),
            secondPageWithSentinel.Select(item => item.Id).OrderBy(id => id));
        Assert.Equal(
            clients[21].Id,
            Assert.Single(outsideFirstPageSearch).Id);
        Assert.Equal(
            new[] { sameNameFirst.Id, sameNameSecond.Id }.OrderBy(id => id),
            sameNameResults.Select(item => item.Id));
        Assert.Equal(4, interceptor.ReaderCommandCount);
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
