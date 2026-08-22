using System.Data.Common;
using Enma.Application.CalendarEvents;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Queries;

[Collection(PostgreSqlCollection.Name)]
public sealed class CalendarEventReadQueriesTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse(
        "2026-08-22T12:00:00Z");

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task FindAsync_UsesSingleNoTrackingTenantQualifiedProjection()
    {
        TenantGraph graphA = CreateGraph("Alpha", "alpha");
        TenantGraph graphB = CreateGraph("Beta", "beta");
        var calendarEvent = new CalendarEvent(
            graphB.Organization.Id,
            "Beta event",
            "Description",
            CreatedAt.AddDays(1),
            CreatedAt.AddDays(1).AddHours(1),
            "Office",
            null,
            graphB.Process.Id,
            graphB.AssigneeMembership.Id,
            graphB.CreatorMembership.Id,
            CreatedAt);
        await SeedAsync(
            graphA.Entities.Concat(graphB.Entities).Append(calendarEvent).ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateContext(interceptor);
        var queries = new CalendarEventReadQueries(dbContext);

        CalendarEventDetailReadModel? crossTenant = await queries.FindAsync(
            calendarEvent.Id,
            graphA.Organization.Id);
        CalendarEventDetailReadModel? sameTenant = await queries.FindAsync(
            calendarEvent.Id,
            graphB.Organization.Id);

        Assert.Null(crossTenant);
        Assert.NotNull(sameTenant);
        Assert.Equal(graphB.Process.Title, sameTenant.ProcessTitle);
        Assert.Equal(graphB.AssigneeUser.Name, sameTenant.AssigneeDisplayName);
        Assert.Equal(2, interceptor.CommandTexts.Count);
        Assert.All(
            interceptor.CommandTexts,
            commandText =>
            {
                Assert.Contains("calendar_events", commandText);
                Assert.Contains("organization_id", commandText);
                Assert.Contains("WHERE", commandText);
                Assert.Contains("LEFT JOIN", commandText);
                Assert.DoesNotContain("SELECT *", commandText);
            });
        Assert.Empty(dbContext.ChangeTracker.Entries());
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

    private static TenantGraph CreateGraph(string name, string slug)
    {
        var organization = new Organization(name, slug, CreatedAt);
        var client = new Client(organization.Id, $"{name} Client", CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            $"{name} Process",
            CreatedAt);
        var creatorUser = new User(
            $"{name} Creator",
            $"{slug}-creator@example.test",
            CreatedAt);
        var creatorMembership = new OrganizationMembership(
            organization.Id,
            creatorUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var assigneeUser = new User(
            $"{name} Assignee",
            $"{slug}-assignee@example.test",
            CreatedAt);
        var assigneeMembership = new OrganizationMembership(
            organization.Id,
            assigneeUser.Id,
            OrganizationRole.Member,
            CreatedAt);

        return new TenantGraph(
            organization,
            legalProcess,
            creatorUser,
            creatorMembership,
            assigneeUser,
            assigneeMembership,
            [
                organization,
                client,
                legalProcess,
                creatorUser,
                creatorMembership,
                assigneeUser,
                assigneeMembership
            ]);
    }

    private sealed record TenantGraph(
        Organization Organization,
        LegalProcess Process,
        User CreatorUser,
        OrganizationMembership CreatorMembership,
        User AssigneeUser,
        OrganizationMembership AssigneeMembership,
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
