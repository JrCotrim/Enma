using System.Data.Common;
using System.Reflection;
using Enma.Application.Agenda;
using Enma.Domain.CalendarEvents;
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
public sealed class UpcomingAgendaReadQueriesTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateOnly ReferenceDate = new(2026, 8, 24);
    private static readonly DateOnly ThroughDate = ReferenceDate.AddDays(7);
    private static readonly DateTimeOffset NowUtc = DateTimeOffset.Parse(
        "2026-08-24T15:00:00Z");
    private static readonly DateTimeOffset EventWindowEndUtc =
        DateTimeOffset.Parse("2026-09-01T00:00:00Z");
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse(
        "2026-08-20T12:00:00Z");

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReadUpcomingAsync_Deadlines_UsesInclusivePendingWindowAndTenantProjection()
    {
        TenantGraph own = CreateGraph("Own deadline", "own-upcoming-deadline");
        TenantGraph foreign = CreateGraph(
            "Foreign deadline",
            "foreign-upcoming-deadline");
        LegalDeadline today = CreateDeadline(own, "Today", ReferenceDate);
        LegalDeadline through = CreateDeadline(own, "Through", ThroughDate);
        LegalDeadline before = CreateDeadline(
            own,
            "Before",
            ReferenceDate.AddDays(-1));
        LegalDeadline after = CreateDeadline(
            own,
            "After",
            ThroughDate.AddDays(1));
        LegalDeadline completed = CreateDeadline(
            own,
            "Completed",
            ReferenceDate.AddDays(1));
        completed.Complete(CreatedAt.AddDays(1));
        LegalDeadline foreignDeadline = CreateDeadline(
            foreign,
            "Foreign",
            ReferenceDate);

        await SeedAsync(
            own.Entities
                .Concat(foreign.Entities)
                .Concat([today, through, before, after, completed, foreignDeadline])
                .ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new AgendaReadQueries(dbContext);

        UpcomingAgendaReadModel result = await queries.ReadUpcomingAsync(
            CreateRequest(own.Organization.Id));

        Assert.Equal([today.Id, through.Id], result.Deadlines.Select(item => item.Id));
        UpcomingAgendaDeadlineReadModel todayItem = result.Deadlines[0];
        Assert.Equal(today.Title, todayItem.Title);
        Assert.Equal(ReferenceDate, todayItem.DueDate);
        Assert.Equal(own.Client.Name, todayItem.ClientName);
        Assert.Equal(own.Process.Title, todayItem.ProcessTitle);
        Assert.Empty(result.Tasks);
        Assert.Empty(result.CalendarEvents);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ReadUpcomingAsync_Deadlines_OrdersByDueDateThenIdAndTakesThree()
    {
        TenantGraph graph = CreateGraph("Deadline limit", "deadline-limit");
        LegalDeadline[] deadlines = Enumerable.Range(1, 4)
            .Select(index => CreateDeadline(
                graph,
                $"Deadline {index}",
                ReferenceDate.AddDays(1)))
            .ToArray();
        SetId(deadlines[0], "00000000-0000-0000-0000-000000000004");
        SetId(deadlines[1], "00000000-0000-0000-0000-000000000002");
        SetId(deadlines[2], "00000000-0000-0000-0000-000000000003");
        SetId(deadlines[3], "00000000-0000-0000-0000-000000000001");

        await SeedAsync(graph.Entities.Concat(deadlines).ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new AgendaReadQueries(dbContext);

        UpcomingAgendaReadModel result = await queries.ReadUpcomingAsync(
            CreateRequest(graph.Organization.Id));

        Assert.Equal(
            [deadlines[3].Id, deadlines[1].Id, deadlines[2].Id],
            result.Deadlines.Select(item => item.Id));
    }

    [Fact]
    public async Task ReadUpcomingAsync_Tasks_UsesPendingDatedWindowAndNullableProjection()
    {
        TenantGraph own = CreateGraph("Own task", "own-upcoming-task");
        TenantGraph foreign = CreateGraph("Foreign task", "foreign-upcoming-task");
        LegalTask today = CreateTask(
            own,
            "Today",
            ReferenceDate,
            own.Process.Id,
            own.AssigneeMembership.Id);
        LegalTask throughStandalone = CreateTask(
            own,
            "Through standalone",
            ThroughDate,
            null,
            null);
        LegalTask before = CreateTask(
            own,
            "Before",
            ReferenceDate.AddDays(-1));
        LegalTask after = CreateTask(
            own,
            "After",
            ThroughDate.AddDays(1));
        LegalTask undated = CreateTask(own, "Undated", null);
        LegalTask completed = CreateTask(
            own,
            "Completed",
            ReferenceDate.AddDays(1));
        completed.Complete(CreatedAt.AddDays(1));
        LegalTask foreignTask = CreateTask(
            foreign,
            "Foreign",
            ReferenceDate);

        await SeedAsync(
            own.Entities
                .Concat(foreign.Entities)
                .Concat(
                    [today, throughStandalone, before, after, undated, completed, foreignTask])
                .ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new AgendaReadQueries(dbContext);

        UpcomingAgendaReadModel result = await queries.ReadUpcomingAsync(
            CreateRequest(own.Organization.Id));

        Assert.Equal(
            [today.Id, throughStandalone.Id],
            result.Tasks.Select(item => item.Id));
        UpcomingAgendaTaskReadModel todayItem = result.Tasks[0];
        Assert.Equal(own.Client.Name, todayItem.ClientName);
        Assert.Equal(own.Process.Title, todayItem.ProcessTitle);
        Assert.Equal(own.AssigneeUser.Name, todayItem.AssigneeDisplayName);
        UpcomingAgendaTaskReadModel standaloneItem = result.Tasks[1];
        Assert.Null(standaloneItem.ClientName);
        Assert.Null(standaloneItem.ProcessTitle);
        Assert.Null(standaloneItem.AssigneeDisplayName);
        Assert.Empty(result.Deadlines);
        Assert.Empty(result.CalendarEvents);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ReadUpcomingAsync_Tasks_OrdersByDueDateThenIdAndTakesThree()
    {
        TenantGraph graph = CreateGraph("Task limit", "task-limit");
        LegalTask[] tasks = Enumerable.Range(1, 4)
            .Select(index => CreateTask(
                graph,
                $"Task {index}",
                ReferenceDate.AddDays(1)))
            .ToArray();
        SetId(tasks[0], "00000000-0000-0000-0000-000000000004");
        SetId(tasks[1], "00000000-0000-0000-0000-000000000002");
        SetId(tasks[2], "00000000-0000-0000-0000-000000000003");
        SetId(tasks[3], "00000000-0000-0000-0000-000000000001");

        await SeedAsync(graph.Entities.Concat(tasks).ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new AgendaReadQueries(dbContext);

        UpcomingAgendaReadModel result = await queries.ReadUpcomingAsync(
            CreateRequest(graph.Organization.Id));

        Assert.Equal(
            [tasks[3].Id, tasks[1].Id, tasks[2].Id],
            result.Tasks.Select(item => item.Id));
    }

    [Fact]
    public async Task ReadUpcomingAsync_CalendarEvents_UsesOperationalUtcWindowAndTenantProjection()
    {
        TenantGraph own = CreateGraph("Own event", "own-upcoming-event");
        TenantGraph foreign = CreateGraph("Foreign event", "foreign-upcoming-event");
        CalendarEvent inProgress = CreateEvent(
            own,
            "In progress",
            NowUtc.AddHours(-2),
            NowUtc.AddHours(1),
            processId: own.Process.Id,
            assigneeMembershipId: own.AssigneeMembership.Id);
        CalendarEvent future = CreateEvent(
            own,
            "Future",
            NowUtc.AddHours(2),
            NowUtc.AddHours(3),
            clientId: own.Client.Id);
        CalendarEvent endsAtNow = CreateEvent(
            own,
            "Ends now",
            NowUtc.AddHours(-1),
            NowUtc);
        CalendarEvent startsAtWindowEnd = CreateEvent(
            own,
            "Starts at end",
            EventWindowEndUtc,
            EventWindowEndUtc.AddHours(1));
        CalendarEvent startsBeforeWindowEnd = CreateEvent(
            own,
            "Starts before end",
            EventWindowEndUtc.AddTicks(-1),
            EventWindowEndUtc.AddHours(1));
        CalendarEvent foreignEvent = CreateEvent(
            foreign,
            "Foreign",
            NowUtc.AddHours(4),
            NowUtc.AddHours(5));

        await SeedAsync(
            own.Entities
                .Concat(foreign.Entities)
                .Concat(
                [
                    inProgress,
                    future,
                    endsAtNow,
                    startsAtWindowEnd,
                    startsBeforeWindowEnd,
                    foreignEvent
                ])
                .ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new AgendaReadQueries(dbContext);

        UpcomingAgendaReadModel result = await queries.ReadUpcomingAsync(
            CreateRequest(own.Organization.Id));

        Assert.Equal(
            [inProgress.Id, future.Id, startsBeforeWindowEnd.Id],
            result.CalendarEvents.Select(item => item.Id));
        UpcomingAgendaCalendarEventReadModel inProgressItem =
            result.CalendarEvents[0];
        Assert.Equal(own.Client.Name, inProgressItem.ClientName);
        Assert.Equal(own.Process.Title, inProgressItem.ProcessTitle);
        Assert.Equal(
            own.AssigneeUser.Name,
            inProgressItem.AssigneeDisplayName);
        UpcomingAgendaCalendarEventReadModel futureItem =
            result.CalendarEvents[1];
        Assert.Equal(own.Client.Name, futureItem.ClientName);
        Assert.Null(futureItem.ProcessTitle);
        Assert.Null(futureItem.AssigneeDisplayName);
        Assert.Empty(result.Deadlines);
        Assert.Empty(result.Tasks);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ReadUpcomingAsync_CalendarEvents_OrdersByStartEndIdAndTakesThree()
    {
        TenantGraph graph = CreateGraph("Event limit", "event-limit");
        DateTimeOffset startsAt = NowUtc.AddHours(1);
        CalendarEvent longest = CreateEvent(
            graph,
            "Longest",
            startsAt,
            startsAt.AddHours(3));
        CalendarEvent tiedSecond = CreateEvent(
            graph,
            "Tied second",
            startsAt,
            startsAt.AddHours(2));
        CalendarEvent shortest = CreateEvent(
            graph,
            "Shortest",
            startsAt,
            startsAt.AddHours(1));
        CalendarEvent tiedFirst = CreateEvent(
            graph,
            "Tied first",
            startsAt,
            startsAt.AddHours(2));
        SetId(tiedSecond, "00000000-0000-0000-0000-000000000002");
        SetId(tiedFirst, "00000000-0000-0000-0000-000000000001");

        await SeedAsync(
            graph.Entities
                .Concat([longest, tiedSecond, shortest, tiedFirst])
                .ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new AgendaReadQueries(dbContext);

        UpcomingAgendaReadModel result = await queries.ReadUpcomingAsync(
            CreateRequest(graph.Organization.Id));

        Assert.Equal(
            [shortest.Id, tiedFirst.Id, tiedSecond.Id],
            result.CalendarEvents.Select(item => item.Id));
    }

    [Fact]
    public async Task ReadUpcomingAsync_ExecutesExactlyThreeBoundedTenantQueriesWithoutTracking()
    {
        TenantGraph graph = CreateGraph("SQL upcoming", "sql-upcoming");
        await SeedAsync(graph.Entities.ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateContext(interceptor);
        var queries = new AgendaReadQueries(dbContext);

        await queries.ReadUpcomingAsync(CreateRequest(graph.Organization.Id));

        Assert.Equal(3, interceptor.CommandTexts.Count);
        Assert.All(
            interceptor.CommandTexts,
            sql =>
            {
                Assert.Contains("organization_id", sql);
                Assert.Contains("LIMIT", sql);
                Assert.DoesNotContain("SELECT *", sql);
            });
        string deadlineSql = Assert.Single(
            interceptor.CommandTexts,
            sql => sql.Contains("FROM legal_deadlines", StringComparison.Ordinal));
        string taskSql = Assert.Single(
            interceptor.CommandTexts,
            sql => sql.Contains("FROM legal_tasks", StringComparison.Ordinal));
        string eventSql = Assert.Single(
            interceptor.CommandTexts,
            sql => sql.Contains("FROM calendar_events", StringComparison.Ordinal));
        Assert.Contains("completed_at IS NULL", deadlineSql);
        Assert.Contains("due_date", deadlineSql);
        Assert.Contains("completed_at IS NULL", taskSql);
        Assert.Contains("due_date IS NOT NULL", taskSql);
        Assert.Contains("ends_at", eventSql);
        Assert.Contains("starts_at", eventSql);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    private static UpcomingAgendaReadRequest CreateRequest(Guid organizationId)
    {
        return new UpcomingAgendaReadRequest(
            organizationId,
            ReferenceDate,
            ThroughDate,
            NowUtc,
            EventWindowEndUtc);
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
            name,
            $"{slugPrefix}-{Guid.NewGuid():N}",
            CreatedAt);
        var creatorUser = new User(
            $"{name} Creator",
            $"{slugPrefix}-creator-{Guid.NewGuid():N}@example.test",
            CreatedAt);
        var creatorMembership = new OrganizationMembership(
            organization.Id,
            creatorUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var assigneeUser = new User(
            $"{name} Assignee",
            $"{slugPrefix}-assignee-{Guid.NewGuid():N}@example.test",
            CreatedAt);
        var assigneeMembership = new OrganizationMembership(
            organization.Id,
            assigneeUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        var client = new Client(
            organization.Id,
            $"{name} Client",
            CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            $"{name} Process",
            CreatedAt);

        return new TenantGraph(
            organization,
            creatorMembership,
            assigneeUser,
            assigneeMembership,
            client,
            legalProcess,
            [
                organization,
                creatorUser,
                creatorMembership,
                assigneeUser,
                assigneeMembership,
                client,
                legalProcess
            ]);
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
        DateOnly? dueDate,
        Guid? processId = null,
        Guid? assigneeMembershipId = null)
    {
        return new LegalTask(
            graph.Organization.Id,
            title,
            null,
            dueDate,
            processId,
            assigneeMembershipId,
            graph.CreatorMembership.Id,
            CreatedAt);
    }

    private static CalendarEvent CreateEvent(
        TenantGraph graph,
        string title,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        Guid? clientId = null,
        Guid? processId = null,
        Guid? assigneeMembershipId = null)
    {
        return new CalendarEvent(
            graph.Organization.Id,
            title,
            null,
            startsAt,
            endsAt,
            null,
            clientId,
            processId,
            assigneeMembershipId,
            graph.CreatorMembership.Id,
            CreatedAt);
    }

    private static void SetId<T>(T entity, string id)
    {
        typeof(T)
            .GetProperty("Id", BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(entity, Guid.Parse(id));
    }

    private sealed record TenantGraph(
        Organization Organization,
        OrganizationMembership CreatorMembership,
        User AssigneeUser,
        OrganizationMembership AssigneeMembership,
        Client Client,
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
