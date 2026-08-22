using System.Data.Common;
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
public sealed class AgendaReadQueriesTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse(
        "2026-08-22T12:00:00Z");
    private static readonly DateTimeOffset FromUtc = DateTimeOffset.Parse(
        "2026-09-01T03:00:00Z");
    private static readonly DateTimeOffset ToUtc = DateTimeOffset.Parse(
        "2026-09-08T03:00:00Z");

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReadAsync_MixedViewport_ProjectsAllSourcesWithExactTemporalSemantics()
    {
        TenantGraph own = CreateGraph("Own", "own");
        TenantGraph foreign = CreateGraph("Foreign", "foreign");

        var completedStartDeadline = new LegalDeadline(
            own.Organization.Id,
            own.Process.Id,
            "Deadline on start",
            new DateOnly(2026, 9, 1),
            CreatedAt);
        completedStartDeadline.Complete(CreatedAt.AddDays(1));
        var lastDeadline = new LegalDeadline(
            own.Organization.Id,
            own.Process.Id,
            "Deadline on last day",
            new DateOnly(2026, 9, 7),
            CreatedAt);
        var endDeadline = new LegalDeadline(
            own.Organization.Id,
            own.Process.Id,
            "Deadline on exclusive end",
            new DateOnly(2026, 9, 8),
            CreatedAt);
        var foreignDeadline = new LegalDeadline(
            foreign.Organization.Id,
            foreign.Process.Id,
            "Foreign deadline",
            new DateOnly(2026, 9, 2),
            CreatedAt);

        var completedProcessTask = new LegalTask(
            own.Organization.Id,
            "Assigned process task",
            null,
            new DateOnly(2026, 9, 1),
            own.Process.Id,
            own.AssigneeMembership.Id,
            own.CreatorMembership.Id,
            CreatedAt);
        completedProcessTask.Complete(CreatedAt.AddDays(1));
        var standaloneTask = new LegalTask(
            own.Organization.Id,
            "Standalone task",
            null,
            new DateOnly(2026, 9, 7),
            null,
            null,
            own.CreatorMembership.Id,
            CreatedAt);
        var endTask = new LegalTask(
            own.Organization.Id,
            "Task on exclusive end",
            null,
            new DateOnly(2026, 9, 8),
            null,
            null,
            own.CreatorMembership.Id,
            CreatedAt);
        var undatedTask = new LegalTask(
            own.Organization.Id,
            "Undated task",
            null,
            null,
            own.Process.Id,
            own.AssigneeMembership.Id,
            own.CreatorMembership.Id,
            CreatedAt);
        var foreignTask = new LegalTask(
            foreign.Organization.Id,
            "Foreign task",
            null,
            new DateOnly(2026, 9, 2),
            foreign.Process.Id,
            foreign.AssigneeMembership.Id,
            foreign.CreatorMembership.Id,
            CreatedAt);

        CalendarEvent generalEvent = CreateEvent(
            own,
            "General event",
            DateTimeOffset.Parse("2026-09-02T12:00:00Z"),
            DateTimeOffset.Parse("2026-09-02T13:00:00Z"));
        CalendarEvent directClientEvent = CreateEvent(
            own,
            "Direct client event",
            DateTimeOffset.Parse("2026-09-03T12:00:00Z"),
            DateTimeOffset.Parse("2026-09-03T13:00:00Z"),
            clientId: own.Client.Id);
        CalendarEvent processEvent = CreateEvent(
            own,
            "Process event",
            DateTimeOffset.Parse("2026-09-04T12:00:00Z"),
            DateTimeOffset.Parse("2026-09-04T13:00:00Z"),
            processId: own.Process.Id,
            assigneeMembershipId: own.AssigneeMembership.Id);
        CalendarEvent startsBefore = CreateEvent(
            own,
            "Starts before and ends inside",
            FromUtc.AddHours(-2),
            FromUtc.AddHours(1));
        CalendarEvent endsAfter = CreateEvent(
            own,
            "Starts inside and ends after",
            ToUtc.AddHours(-1),
            ToUtc.AddHours(2));
        CalendarEvent spansViewport = CreateEvent(
            own,
            "Spans viewport",
            FromUtc.AddDays(-1),
            ToUtc.AddDays(1));
        CalendarEvent endsAtStart = CreateEvent(
            own,
            "Ends at start",
            FromUtc.AddHours(-1),
            FromUtc);
        CalendarEvent startsAtEnd = CreateEvent(
            own,
            "Starts at end",
            ToUtc,
            ToUtc.AddHours(1));
        CalendarEvent foreignEvent = CreateEvent(
            foreign,
            "Foreign event",
            DateTimeOffset.Parse("2026-09-05T12:00:00Z"),
            DateTimeOffset.Parse("2026-09-05T13:00:00Z"),
            processId: foreign.Process.Id,
            assigneeMembershipId: foreign.AssigneeMembership.Id);

        await SeedAsync(
            own.Entities
                .Concat(foreign.Entities)
                .Concat(
                [
                    completedStartDeadline,
                    lastDeadline,
                    endDeadline,
                    foreignDeadline,
                    completedProcessTask,
                    standaloneTask,
                    endTask,
                    undatedTask,
                    foreignTask,
                    generalEvent,
                    directClientEvent,
                    processEvent,
                    startsBefore,
                    endsAfter,
                    spansViewport,
                    endsAtStart,
                    startsAtEnd,
                    foreignEvent
                ])
                .ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateContext(interceptor);
        var queries = new AgendaReadQueries(dbContext);

        IReadOnlyList<AgendaItemReadModel> items = await queries.ReadAsync(
            CreateRequest(own.Organization.Id));

        Assert.Equal(10, items.Count);
        Assert.Contains(items, item => item.Id == completedStartDeadline.Id);
        Assert.Contains(items, item => item.Id == lastDeadline.Id);
        Assert.Contains(items, item => item.Id == completedProcessTask.Id);
        Assert.Contains(items, item => item.Id == standaloneTask.Id);
        Assert.Contains(items, item => item.Id == generalEvent.Id);
        Assert.Contains(items, item => item.Id == directClientEvent.Id);
        Assert.Contains(items, item => item.Id == processEvent.Id);
        Assert.Contains(items, item => item.Id == startsBefore.Id);
        Assert.Contains(items, item => item.Id == endsAfter.Id);
        Assert.Contains(items, item => item.Id == spansViewport.Id);
        Assert.DoesNotContain(items, item => item.Id == endDeadline.Id);
        Assert.DoesNotContain(items, item => item.Id == endTask.Id);
        Assert.DoesNotContain(items, item => item.Id == undatedTask.Id);
        Assert.DoesNotContain(items, item => item.Id == endsAtStart.Id);
        Assert.DoesNotContain(items, item => item.Id == startsAtEnd.Id);
        Assert.DoesNotContain(items, item => item.Id == foreignDeadline.Id);
        Assert.DoesNotContain(items, item => item.Id == foreignTask.Id);
        Assert.DoesNotContain(items, item => item.Id == foreignEvent.Id);

        AgendaItemReadModel deadline = Assert.Single(
            items,
            item => item.Id == completedStartDeadline.Id);
        Assert.Equal(AgendaItemKind.Deadline, deadline.Kind);
        Assert.True(deadline.IsAllDay);
        Assert.Equal(new DateOnly(2026, 9, 1), deadline.Date);
        Assert.Null(deadline.StartsAt);
        Assert.Null(deadline.EndsAt);
        Assert.Equal(completedStartDeadline.CompletedAt, deadline.CompletedAt);
        Assert.Equal(own.Process.Id, deadline.ProcessId);
        Assert.Equal(own.Process.Title, deadline.ProcessTitle);
        Assert.Equal(own.Client.Id, deadline.ClientId);
        Assert.Equal(own.Client.Name, deadline.ClientName);
        Assert.Null(deadline.AssigneeMembershipId);

        AgendaItemReadModel task = Assert.Single(
            items,
            item => item.Id == completedProcessTask.Id);
        Assert.Equal(AgendaItemKind.Task, task.Kind);
        Assert.True(task.IsAllDay);
        Assert.Equal(new DateOnly(2026, 9, 1), task.Date);
        Assert.Null(task.StartsAt);
        Assert.Null(task.EndsAt);
        Assert.Equal(completedProcessTask.CompletedAt, task.CompletedAt);
        Assert.Equal(own.Process.Id, task.ProcessId);
        Assert.Equal(own.Client.Id, task.ClientId);
        Assert.Equal(own.AssigneeMembership.Id, task.AssigneeMembershipId);
        Assert.Equal(own.AssigneeUser.Name, task.AssigneeDisplayName);

        AgendaItemReadModel standalone = Assert.Single(
            items,
            item => item.Id == standaloneTask.Id);
        Assert.Null(standalone.ProcessId);
        Assert.Null(standalone.ProcessTitle);
        Assert.Null(standalone.ClientId);
        Assert.Null(standalone.ClientName);
        Assert.Null(standalone.CompletedAt);

        AgendaItemReadModel general = Assert.Single(
            items,
            item => item.Id == generalEvent.Id);
        Assert.Equal(AgendaItemKind.CalendarEvent, general.Kind);
        Assert.False(general.IsAllDay);
        Assert.Null(general.Date);
        Assert.Equal(generalEvent.StartsAt, general.StartsAt);
        Assert.Equal(generalEvent.EndsAt, general.EndsAt);
        Assert.Null(general.CompletedAt);
        Assert.Null(general.ClientId);
        Assert.Null(general.ProcessId);

        AgendaItemReadModel directClient = Assert.Single(
            items,
            item => item.Id == directClientEvent.Id);
        Assert.Equal(own.Client.Id, directClient.ClientId);
        Assert.Equal(own.Client.Name, directClient.ClientName);
        Assert.Null(directClient.ProcessId);

        AgendaItemReadModel process = Assert.Single(
            items,
            item => item.Id == processEvent.Id);
        Assert.Equal(own.Process.Id, process.ProcessId);
        Assert.Equal(own.Process.Title, process.ProcessTitle);
        Assert.Equal(own.Client.Id, process.ClientId);
        Assert.Equal(own.Client.Name, process.ClientName);
        Assert.Equal(own.AssigneeMembership.Id,
            process.AssigneeMembershipId);
        Assert.Equal(own.AssigneeUser.Name, process.AssigneeDisplayName);

        Assert.Equal(3, interceptor.CommandTexts.Count);
        AssertSqlShape(interceptor.CommandTexts);
        Assert.Empty(dbContext.ChangeTracker.Entries());

        interceptor.Clear();
        IReadOnlyList<AgendaItemReadModel> empty = await queries.ReadAsync(
            CreateRequest(Guid.NewGuid()));

        Assert.Empty(empty);
        Assert.Equal(3, interceptor.CommandTexts.Count);
        AssertSqlShape(interceptor.CommandTexts);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    private static void AssertSqlShape(IReadOnlyList<string> commandTexts)
    {
        string deadlineSql = Assert.Single(
            commandTexts,
            text => text.Contains("FROM legal_deadlines", StringComparison.Ordinal));
        string taskSql = Assert.Single(
            commandTexts,
            text => text.Contains("FROM legal_tasks", StringComparison.Ordinal));
        string eventSql = Assert.Single(
            commandTexts,
            text => text.Contains("FROM calendar_events", StringComparison.Ordinal));

        Assert.Contains("organization_id", deadlineSql);
        Assert.Contains("due_date", deadlineSql);
        Assert.Contains(">=", deadlineSql);
        Assert.Contains("<", deadlineSql);
        Assert.Contains("JOIN legal_processes", deadlineSql);
        Assert.Contains("JOIN clients", deadlineSql);

        Assert.Contains("organization_id", taskSql);
        Assert.Contains("due_date IS NOT NULL", taskSql);
        Assert.Contains(">=", taskSql);
        Assert.Contains("<", taskSql);
        Assert.Contains("LEFT JOIN legal_processes", taskSql);
        Assert.Contains("LEFT JOIN clients", taskSql);
        Assert.Contains("LEFT JOIN organization_memberships", taskSql);

        Assert.Contains("organization_id", eventSql);
        Assert.Contains("starts_at", eventSql);
        Assert.Contains("ends_at", eventSql);
        Assert.Contains("<", eventSql);
        Assert.Contains(">", eventSql);
        Assert.Contains("LEFT JOIN legal_processes", eventSql);
        Assert.Contains("LEFT JOIN clients", eventSql);
        Assert.Contains("LEFT JOIN organization_memberships", eventSql);

        Assert.All(
            commandTexts,
            commandText => Assert.DoesNotContain("SELECT *", commandText));
    }

    private static AgendaReadRequest CreateRequest(Guid organizationId)
    {
        return new AgendaReadRequest(
            organizationId,
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 8),
            FromUtc,
            ToUtc);
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
        var client = new Client(
            organization.Id,
            $"{name} Client",
            CreatedAt);
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
            client,
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

    private sealed record TenantGraph(
        Organization Organization,
        Client Client,
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

        public void Clear() => _commandTexts.Clear();

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
