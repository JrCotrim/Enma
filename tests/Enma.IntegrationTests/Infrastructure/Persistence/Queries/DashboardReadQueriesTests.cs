using System.Data.Common;
using Enma.Application.Dashboard;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Queries;

[Collection(PostgreSqlCollection.Name)]
public sealed class DashboardReadQueriesTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateOnly ReferenceDate = new(2026, 8, 24);
    private static readonly DateOnly ThroughDate = ReferenceDate.AddDays(7);
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse(
        "2026-08-20T12:00:00Z");

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReadMetricsAsync_MixedOperationalData_ReturnsExactTenantScopedBuckets()
    {
        TenantGraph own = CreateGraph("Own", "own-dashboard");
        TenantGraph foreign = CreateGraph("Foreign", "foreign-dashboard");
        var ownInactiveClient = new Client(
            own.Organization.Id,
            "Own inactive client",
            CreatedAt);
        ownInactiveClient.Deactivate();
        var inactiveClientProcess = new LegalProcess(
            own.Organization.Id,
            ownInactiveClient.Id,
            "Process for inactive client",
            CreatedAt);
        var foreignExtraClient = new Client(
            foreign.Organization.Id,
            "Foreign extra client",
            CreatedAt);

        LegalDeadline[] ownDeadlines =
        [
            CreateDeadline(own, "Overdue", ReferenceDate.AddDays(-1)),
            CreateDeadline(own, "Today", ReferenceDate),
            CreateDeadline(own, "Tomorrow", ReferenceDate.AddDays(1)),
            CreateDeadline(own, "Through", ThroughDate),
            CreateDeadline(own, "After", ThroughDate.AddDays(1)),
            CreateDeadline(own, "Completed", ReferenceDate.AddDays(-2))
        ];
        ownDeadlines[^1].Complete(CreatedAt.AddDays(1));

        LegalTask[] ownTasks =
        [
            CreateTask(own, "Overdue", ReferenceDate.AddDays(-1)),
            CreateTask(own, "Today", ReferenceDate),
            CreateTask(own, "Tomorrow", ReferenceDate.AddDays(1)),
            CreateTask(own, "Through", ThroughDate),
            CreateTask(own, "After", ThroughDate.AddDays(1)),
            CreateTask(own, "Undated", null),
            CreateTask(own, "Completed", ReferenceDate.AddDays(-2))
        ];
        ownTasks[^1].Complete(CreatedAt.AddDays(1));

        LegalDeadline foreignDeadline = CreateDeadline(
            foreign,
            "Foreign overdue",
            ReferenceDate.AddDays(-1));
        LegalTask foreignTask = CreateTask(
            foreign,
            "Foreign today",
            ReferenceDate);

        await SeedAsync(
            own.Entities
                .Concat(foreign.Entities)
                .Concat(
                [
                    ownInactiveClient,
                    inactiveClientProcess,
                    foreignExtraClient,
                    .. ownDeadlines,
                    .. ownTasks,
                    foreignDeadline,
                    foreignTask
                ])
                .ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new DashboardReadQueries(dbContext);

        DashboardMetricsReadModel result = await queries.ReadMetricsAsync(
            CreateRequest(own.Organization.Id));

        Assert.Equal(1, result.ActiveClients);
        Assert.Equal(2, result.TotalLegalProcesses);
        Assert.Equal(5, result.PendingDeadlines);
        Assert.Equal(6, result.PendingTasks);
        Assert.Equal(1, result.OverdueDeadlines);
        Assert.Equal(1, result.DeadlinesDueToday);
        Assert.Equal(2, result.DeadlinesDueInNextSevenDays);
        Assert.Equal(1, result.OverdueTasks);
        Assert.Equal(1, result.TasksDueToday);
        Assert.Equal(2, result.TasksDueInNextSevenDays);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ReadMetricsAsync_OrganizationWithoutOperationalData_ReturnsZeros()
    {
        var organization = new Organization(
            "Empty Dashboard",
            $"empty-dashboard-{Guid.NewGuid():N}",
            CreatedAt);
        await SeedAsync(organization);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new DashboardReadQueries(dbContext);

        DashboardMetricsReadModel result = await queries.ReadMetricsAsync(
            CreateRequest(organization.Id));

        Assert.Equal(
            new DashboardMetricsReadModel(0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            result);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ReadMetricsAsync_UsesOneIndependentAggregateCommandWithoutCartesianJoin()
    {
        TenantGraph graph = CreateGraph("SQL", "sql-dashboard");
        await SeedAsync(graph.Entities.ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateContext(interceptor);
        var queries = new DashboardReadQueries(dbContext);

        await queries.ReadMetricsAsync(CreateRequest(graph.Organization.Id));

        string sql = Assert.Single(interceptor.CommandTexts);
        Assert.Equal(1, CountOccurrences(sql, "FROM clients"));
        Assert.Equal(1, CountOccurrences(sql, "FROM legal_processes"));
        Assert.Equal(4, CountOccurrences(sql, "FROM legal_deadlines"));
        Assert.Equal(4, CountOccurrences(sql, "FROM legal_tasks"));
        Assert.True(CountOccurrences(sql, "organization_id") >= 10);
        Assert.DoesNotContain(" JOIN ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CROSS JOIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DISTINCT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    private static DashboardMetricsReadRequest CreateRequest(
        Guid organizationId)
    {
        return new DashboardMetricsReadRequest(
            organizationId,
            ReferenceDate,
            ThroughDate);
    }

    private EnmaDbContext CreateContext(DbCommandInterceptor interceptor)
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

    private static TenantGraph CreateGraph(string name, string slugPrefix)
    {
        var organization = new Organization(
            $"{name} Dashboard",
            $"{slugPrefix}-{Guid.NewGuid():N}",
            CreatedAt);
        var user = new User(
            $"{name} User",
            $"{slugPrefix}-{Guid.NewGuid():N}@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var client = new Client(
            organization.Id,
            $"{name} active client",
            CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            $"{name} process",
            CreatedAt);

        return new TenantGraph(
            organization,
            membership,
            legalProcess,
            [organization, user, membership, client, legalProcess]);
    }

    private static LegalDeadline CreateDeadline(
        TenantGraph graph,
        string title,
        DateOnly dueDate)
    {
        return new LegalDeadline(
            graph.Organization.Id,
            graph.Process.Id,
            title,
            dueDate,
            CreatedAt);
    }

    private static LegalTask CreateTask(
        TenantGraph graph,
        string title,
        DateOnly? dueDate)
    {
        return new LegalTask(
            graph.Organization.Id,
            title,
            null,
            dueDate,
            graph.Process.Id,
            graph.Membership.Id,
            graph.Membership.Id,
            CreatedAt);
    }

    private static int CountOccurrences(string value, string search)
    {
        return value.Split(search, StringSplitOptions.None).Length - 1;
    }

    private sealed record TenantGraph(
        Organization Organization,
        OrganizationMembership Membership,
        LegalProcess Process,
        IReadOnlyList<object> Entities);

    private sealed class ReaderCommandInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commandTexts = [];

        public IReadOnlyList<string> CommandTexts => _commandTexts;

        public override ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commandTexts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
