using System.Data.Common;
using Enma.Application.Tasks;
using Enma.Domain.Clients;
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
public sealed class LegalTaskReadQueriesTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        14,
        20,
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
    public async Task FindAsync_WithHistoricalRelationsAndInactiveClient_ReturnsApprovedProjection()
    {
        TenantGraph graph = CreateGraph("Alpha", "alpha");
        graph.CreatorMembership.Deactivate();
        graph.CreatorUser.Deactivate();
        graph.AssigneeMembership.Deactivate();
        graph.AssigneeUser.Deactivate();
        graph.Client.Deactivate();
        var legalTask = CreateTask(
            graph,
            "Historical task",
            new DateOnly(2026, 9, 1),
            graph.Process.Id,
            graph.AssigneeMembership.Id);
        await SeedAsync(graph.Entities.Append(legalTask).ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateInterceptedContext(interceptor);
        var queries = new LegalTaskReadQueries(dbContext);

        LegalTaskDetailReadModel? result = await queries.FindAsync(
            legalTask.Id,
            graph.Organization.Id);
        LegalTaskListReadPage list = await queries.ListAsync(
            Request(graph.Organization.Id, LegalTaskState.Pending));

        Assert.NotNull(result);
        Assert.Equal(legalTask.Id, result.Id);
        Assert.Equal("Historical task", result.Title);
        Assert.Equal("Task description", result.Description);
        Assert.Equal(graph.Process.Id, result.ProcessId);
        Assert.Equal(graph.Process.Title, result.ProcessTitle);
        Assert.Equal(graph.Client.Name, result.ClientName);
        Assert.Equal(graph.AssigneeMembership.Id, result.AssigneeMembershipId);
        Assert.Equal(graph.AssigneeUser.Name, result.AssigneeDisplayName);
        Assert.Equal(graph.CreatorMembership.Id, result.CreatedByMembershipId);
        Assert.Equal(graph.CreatorUser.Name, result.CreatedByDisplayName);
        Assert.Equal(LegalTaskState.Pending, result.State);
        Assert.Equal([legalTask.Id], list.Items.Select(item => item.Id));
        Assert.Equal(graph.Client.Name, list.Items.Single().ClientName);
        Assert.Equal(graph.AssigneeUser.Name,
            list.Items.Single().AssigneeDisplayName);
        Assert.Equal(2, interceptor.ReaderCommandCount);
        Assert.All(interceptor.CommandTexts, AssertTenantSafeProjectionSql);
    }

    [Fact]
    public async Task FindAsync_WithGeneralTask_ReturnsNullProcessAndAssigneeData()
    {
        TenantGraph graph = CreateGraph("Alpha", "alpha");
        var legalTask = CreateTask(
            graph,
            "General task",
            null,
            processId: null,
            assigneeMembershipId: null);
        await SeedAsync(graph.Entities.Append(legalTask).ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new LegalTaskReadQueries(dbContext);

        LegalTaskDetailReadModel? result = await queries.FindAsync(
            legalTask.Id,
            graph.Organization.Id);
        LegalTaskListReadPage list = await queries.ListAsync(
            Request(graph.Organization.Id, LegalTaskState.Pending));

        Assert.NotNull(result);
        Assert.Null(result.ProcessId);
        Assert.Null(result.ProcessTitle);
        Assert.Null(result.ClientName);
        Assert.Null(result.AssigneeMembershipId);
        Assert.Null(result.AssigneeDisplayName);
        Assert.Equal([legalTask.Id], list.Items.Select(item => item.Id));
        Assert.Null(list.Items.Single().ProcessTitle);
        Assert.Null(list.Items.Single().ClientName);
    }

    [Fact]
    public async Task FindAsync_WithMissingOrCrossTenantTask_ReturnsNullFromTenantBoundQuery()
    {
        TenantGraph graphA = CreateGraph("Alpha", "alpha");
        TenantGraph graphB = CreateGraph("Beta", "beta");
        LegalTask taskB = CreateTask(graphB, "Beta task");
        await SeedAsync(
            graphA.Entities
                .Concat(graphB.Entities)
                .Append(taskB)
                .ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateInterceptedContext(interceptor);
        var queries = new LegalTaskReadQueries(dbContext);

        LegalTaskDetailReadModel? missing = await queries.FindAsync(
            Guid.NewGuid(),
            graphA.Organization.Id);
        LegalTaskDetailReadModel? crossTenant = await queries.FindAsync(
            taskB.Id,
            graphA.Organization.Id);

        Assert.Null(missing);
        Assert.Null(crossTenant);
        Assert.Equal(2, interceptor.ReaderCommandCount);
        Assert.All(
            interceptor.CommandTexts,
            commandText =>
            {
                Assert.Contains("legal_tasks", commandText);
                Assert.Contains("organization_id", commandText);
                Assert.Contains("id", commandText);
            });
    }

    [Fact]
    public async Task ListAsync_Pending_OrdersDueDateNullsLastAndPaginatesDeterministically()
    {
        TenantGraph graph = CreateGraph("Alpha", "alpha");
        LegalTask earliest = CreateTask(
            graph,
            "Earliest",
            new DateOnly(2026, 8, 20),
            createdAt: CreatedAt.AddMinutes(1));
        LegalTask tiedNewest = CreateTask(
            graph,
            "Tied newest",
            new DateOnly(2026, 8, 21),
            createdAt: CreatedAt.AddMinutes(4));
        LegalTask tiedFirstId = CreateTask(
            graph,
            "Tied first id",
            new DateOnly(2026, 8, 21),
            createdAt: CreatedAt.AddMinutes(3));
        LegalTask tiedSecondId = CreateTask(
            graph,
            "Tied second id",
            new DateOnly(2026, 8, 21),
            createdAt: CreatedAt.AddMinutes(3));
        LegalTask noDueDate = CreateTask(
            graph,
            "No due date",
            null,
            createdAt: CreatedAt.AddMinutes(5));
        LegalTask completed = CreateTask(
            graph,
            "Completed",
            new DateOnly(2026, 8, 19));
        completed.Complete(CreatedAt.AddDays(1));
        SetId(tiedFirstId, Guid.Parse("00000000-0000-0000-0000-000000000001"));
        SetId(tiedSecondId, Guid.Parse("00000000-0000-0000-0000-000000000002"));
        await SeedAsync(
            graph.Entities
                .Concat([earliest, tiedNewest, tiedFirstId, tiedSecondId, noDueDate,
                    completed])
                .ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateInterceptedContext(interceptor);
        var queries = new LegalTaskReadQueries(dbContext);

        LegalTaskListReadPage firstPage = await queries.ListAsync(
            Request(graph.Organization.Id, LegalTaskState.Pending, pageSize: 3));
        LegalTaskListReadPage secondPage = await queries.ListAsync(
            Request(
                graph.Organization.Id,
                LegalTaskState.Pending,
                pageNumber: 2,
                pageSize: 3));

        Assert.Equal(
            [earliest.Id, tiedNewest.Id, tiedFirstId.Id],
            firstPage.Items.Select(item => item.Id));
        Assert.True(firstPage.HasNext);
        Assert.Equal(
            [tiedSecondId.Id, noDueDate.Id],
            secondPage.Items.Select(item => item.Id));
        Assert.False(secondPage.HasNext);
        Assert.DoesNotContain(
            firstPage.Items.Concat(secondPage.Items),
            item => item.Id == completed.Id);
        Assert.Equal(2, interceptor.ReaderCommandCount);
        AssertPendingOrderingSql(interceptor.CommandTexts[0]);
        Assert.DoesNotContain("COUNT", interceptor.CommandTexts[0]);
    }

    [Fact]
    public async Task ListAsync_Completed_OrdersCompletedAtDescendingAndPaginates()
    {
        TenantGraph graph = CreateGraph("Alpha", "alpha");
        LegalTask latest = CreateCompletedTask(
            graph,
            "Latest",
            CreatedAt.AddDays(3));
        LegalTask tiedFirstId = CreateCompletedTask(
            graph,
            "Tied first id",
            CreatedAt.AddDays(2));
        LegalTask tiedSecondId = CreateCompletedTask(
            graph,
            "Tied second id",
            CreatedAt.AddDays(2));
        LegalTask pending = CreateTask(graph, "Pending");
        SetId(tiedFirstId, Guid.Parse("00000000-0000-0000-0000-000000000001"));
        SetId(tiedSecondId, Guid.Parse("00000000-0000-0000-0000-000000000002"));
        await SeedAsync(
            graph.Entities
                .Concat([latest, tiedFirstId, tiedSecondId, pending])
                .ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateInterceptedContext(interceptor);
        var queries = new LegalTaskReadQueries(dbContext);

        LegalTaskListReadPage firstPage = await queries.ListAsync(
            Request(graph.Organization.Id, LegalTaskState.Completed, pageSize: 2));
        LegalTaskListReadPage secondPage = await queries.ListAsync(
            Request(
                graph.Organization.Id,
                LegalTaskState.Completed,
                pageNumber: 2,
                pageSize: 2));

        Assert.Equal(
            [latest.Id, tiedFirstId.Id],
            firstPage.Items.Select(item => item.Id));
        Assert.True(firstPage.HasNext);
        Assert.Equal([tiedSecondId.Id], secondPage.Items.Select(item => item.Id));
        Assert.False(secondPage.HasNext);
        Assert.DoesNotContain(firstPage.Items, item => item.Id == pending.Id);
        Assert.Contains("completed_at", interceptor.CommandTexts[0]);
        Assert.Contains("DESC", interceptor.CommandTexts[0]);
        Assert.DoesNotContain("COUNT", interceptor.CommandTexts[0]);
    }

    [Fact]
    public async Task ListAsync_ProcessFilters_UseCollectionSemanticsWithoutValidationQueries()
    {
        TenantGraph graph = CreateGraph("Alpha", "alpha");
        var secondClient = new Client(
            graph.Organization.Id,
            "Second client",
            CreatedAt);
        var secondProcess = new LegalProcess(
            graph.Organization.Id,
            secondClient.Id,
            "Second process",
            CreatedAt);
        TenantGraph otherGraph = CreateGraph("Beta", "beta");
        LegalTask first = CreateTask(
            graph,
            "First process",
            processId: graph.Process.Id);
        LegalTask second = CreateTask(
            graph,
            "Second process",
            processId: secondProcess.Id);
        LegalTask general = CreateTask(graph, "General", processId: null);
        await SeedAsync(
            graph.Entities
                .Concat([secondClient, secondProcess, first, second, general])
                .Concat(otherGraph.Entities)
                .ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateInterceptedContext(interceptor);
        var queries = new LegalTaskReadQueries(dbContext);

        LegalTaskListReadPage any = await queries.ListAsync(
            Request(graph.Organization.Id, LegalTaskState.Pending));
        LegalTaskListReadPage firstOnly = await queries.ListAsync(
            Request(
                graph.Organization.Id,
                LegalTaskState.Pending,
                processId: graph.Process.Id));
        LegalTaskListReadPage crossTenant = await queries.ListAsync(
            Request(
                graph.Organization.Id,
                LegalTaskState.Pending,
                processId: otherGraph.Process.Id));
        LegalTaskListReadPage missing = await queries.ListAsync(
            Request(
                graph.Organization.Id,
                LegalTaskState.Pending,
                processId: Guid.NewGuid()));

        Assert.Equal(3, any.Items.Count);
        Assert.Equal([first.Id], firstOnly.Items.Select(item => item.Id));
        Assert.Empty(crossTenant.Items);
        Assert.Empty(missing.Items);
        Assert.Equal(4, interceptor.ReaderCommandCount);
    }

    [Fact]
    public async Task ListAsync_AssigneeFilters_IncludeHistoricalAndUseMembershipIdentity()
    {
        TenantGraph graph = CreateGraph("Alpha", "alpha");
        var activeUser = new User(
            "Active assignee",
            "active.assignee@example.test",
            CreatedAt);
        var activeMembership = new OrganizationMembership(
            graph.Organization.Id,
            activeUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        graph.AssigneeMembership.Deactivate();
        graph.AssigneeUser.Deactivate();
        LegalTask unassigned = CreateTask(graph, "Unassigned");
        LegalTask active = CreateTask(
            graph,
            "Active",
            assigneeMembershipId: activeMembership.Id);
        LegalTask historical = CreateTask(
            graph,
            "Historical",
            assigneeMembershipId: graph.AssigneeMembership.Id);
        TenantGraph otherGraph = CreateGraph("Beta", "beta");
        await SeedAsync(
            graph.Entities
                .Concat([activeUser, activeMembership, unassigned, active, historical])
                .Concat(otherGraph.Entities)
                .ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new LegalTaskReadQueries(dbContext);

        LegalTaskListReadPage any = await queries.ListAsync(
            Request(graph.Organization.Id, LegalTaskState.Pending));
        LegalTaskListReadPage unassignedOnly = await queries.ListAsync(
            Request(
                graph.Organization.Id,
                LegalTaskState.Pending,
                assigneeKind: LegalTaskReadAssigneeFilterKind.Unassigned));
        LegalTaskListReadPage historicalOnly = await queries.ListAsync(
            Request(
                graph.Organization.Id,
                LegalTaskState.Pending,
                assigneeKind: LegalTaskReadAssigneeFilterKind.Membership,
                assigneeMembershipId: graph.AssigneeMembership.Id));
        LegalTaskListReadPage crossTenant = await queries.ListAsync(
            Request(
                graph.Organization.Id,
                LegalTaskState.Pending,
                assigneeKind: LegalTaskReadAssigneeFilterKind.Membership,
                assigneeMembershipId: otherGraph.AssigneeMembership.Id));
        LegalTaskListReadPage missing = await queries.ListAsync(
            Request(
                graph.Organization.Id,
                LegalTaskState.Pending,
                assigneeKind: LegalTaskReadAssigneeFilterKind.Membership,
                assigneeMembershipId: Guid.NewGuid()));

        Assert.Equal(3, any.Items.Count);
        Assert.Contains(any.Items, item => item.Id == historical.Id &&
            item.AssigneeDisplayName == graph.AssigneeUser.Name);
        Assert.Equal([unassigned.Id], unassignedOnly.Items.Select(item => item.Id));
        Assert.Equal([historical.Id], historicalOnly.Items.Select(item => item.Id));
        Assert.Empty(crossTenant.Items);
        Assert.Empty(missing.Items);
        Assert.NotEqual(
            graph.AssigneeUser.Id,
            historicalOnly.Items.Single().AssigneeMembershipId);
    }

    [Fact]
    public async Task ListAsync_StateProcessAndAssigneeFilters_ComposeInOneQuery()
    {
        TenantGraph graph = CreateGraph("Alpha", "alpha");
        LegalTask matching = CreateTask(
            graph,
            "Matching",
            processId: graph.Process.Id,
            assigneeMembershipId: graph.AssigneeMembership.Id);
        LegalTask wrongAssignee = CreateTask(
            graph,
            "Wrong assignee",
            processId: graph.Process.Id);
        LegalTask completed = CreateTask(
            graph,
            "Completed",
            processId: graph.Process.Id,
            assigneeMembershipId: graph.AssigneeMembership.Id);
        completed.Complete(CreatedAt.AddDays(1));
        await SeedAsync(
            graph.Entities.Concat([matching, wrongAssignee, completed]).ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateInterceptedContext(interceptor);
        var queries = new LegalTaskReadQueries(dbContext);

        LegalTaskListReadPage pendingResult = await queries.ListAsync(
            Request(
                graph.Organization.Id,
                LegalTaskState.Pending,
                graph.Process.Id,
                LegalTaskReadAssigneeFilterKind.Membership,
                graph.AssigneeMembership.Id));
        LegalTaskListReadPage completedResult = await queries.ListAsync(
            Request(
                graph.Organization.Id,
                LegalTaskState.Completed,
                graph.Process.Id,
                LegalTaskReadAssigneeFilterKind.Membership,
                graph.AssigneeMembership.Id));

        Assert.Equal([matching.Id], pendingResult.Items.Select(item => item.Id));
        Assert.Equal([completed.Id], completedResult.Items.Select(item => item.Id));
        Assert.Equal(2, interceptor.ReaderCommandCount);
        Assert.All(
            interceptor.CommandTexts,
            commandText =>
            {
                Assert.Contains("process_id", commandText);
                Assert.Contains("assignee_membership_id", commandText);
                Assert.Contains("completed_at", commandText);
            });
    }

    private EnmaDbContext CreateInterceptedContext(
        DbCommandInterceptor interceptor)
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

    private static LegalTaskListReadRequest Request(
        Guid organizationId,
        LegalTaskState state,
        Guid? processId = null,
        LegalTaskReadAssigneeFilterKind assigneeKind =
            LegalTaskReadAssigneeFilterKind.Any,
        Guid? assigneeMembershipId = null,
        int pageNumber = 1,
        int pageSize = 20)
    {
        return new LegalTaskListReadRequest(
            organizationId,
            state,
            processId,
            assigneeKind,
            assigneeMembershipId,
            pageNumber,
            pageSize);
    }

    private static LegalTask CreateTask(
        TenantGraph graph,
        string title,
        DateOnly? dueDate = null,
        Guid? processId = null,
        Guid? assigneeMembershipId = null,
        DateTimeOffset? createdAt = null)
    {
        return new LegalTask(
            graph.Organization.Id,
            title,
            "Task description",
            dueDate,
            processId,
            assigneeMembershipId,
            graph.CreatorMembership.Id,
            createdAt ?? CreatedAt);
    }

    private static LegalTask CreateCompletedTask(
        TenantGraph graph,
        string title,
        DateTimeOffset completedAt)
    {
        LegalTask legalTask = CreateTask(graph, title);
        legalTask.Complete(completedAt);
        return legalTask;
    }

    private static TenantGraph CreateGraph(string name, string slug)
    {
        var organization = new Organization(name, slug, CreatedAt);
        var creatorUser = new User(
            $"{name} Creator",
            $"{slug}.creator@example.test",
            CreatedAt);
        var creatorMembership = new OrganizationMembership(
            organization.Id,
            creatorUser.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        var assigneeUser = new User(
            $"{name} Assignee",
            $"{slug}.assignee@example.test",
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
        var process = new LegalProcess(
            organization.Id,
            client.Id,
            $"{name} Process",
            CreatedAt);

        return new TenantGraph(
            organization,
            creatorUser,
            creatorMembership,
            assigneeUser,
            assigneeMembership,
            client,
            process);
    }

    private static void SetId(LegalTask legalTask, Guid id)
    {
        typeof(LegalTask)
            .GetProperty(nameof(LegalTask.Id))!
            .SetValue(legalTask, id);
    }

    private static void AssertTenantSafeProjectionSql(string commandText)
    {
        Assert.Contains("legal_tasks", commandText);
        Assert.Contains("organization_id", commandText);
        Assert.Contains("legal_processes", commandText);
        Assert.Contains("clients", commandText);
        Assert.Contains("organization_memberships", commandText);
        Assert.Contains("users", commandText);
        Assert.Contains("LEFT JOIN", commandText);
        Assert.DoesNotContain("is_active", commandText);
        Assert.DoesNotContain("email", commandText);
        Assert.DoesNotContain("COUNT", commandText);
        Assert.DoesNotContain("FOR UPDATE", commandText);
    }

    private static void AssertPendingOrderingSql(string commandText)
    {
        Assert.Contains("ORDER BY", commandText);
        Assert.Contains("due_date", commandText);
        Assert.Contains("created_at", commandText);
        Assert.Contains("DESC", commandText);
        Assert.Contains("LIMIT", commandText);
        Assert.Contains("OFFSET", commandText);
    }

    private sealed record TenantGraph(
        Organization Organization,
        User CreatorUser,
        OrganizationMembership CreatorMembership,
        User AssigneeUser,
        OrganizationMembership AssigneeMembership,
        Client Client,
        LegalProcess Process)
    {
        public object[] Entities =>
        [
            Organization,
            CreatorUser,
            CreatorMembership,
            AssigneeUser,
            AssigneeMembership,
            Client,
            Process
        ];
    }

    private sealed class ReaderCommandInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commandTexts = [];

        public int ReaderCommandCount => _commandTexts.Count;

        public IReadOnlyList<string> CommandTexts => _commandTexts;

        public string LastCommandText => _commandTexts.LastOrDefault() ?? string.Empty;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
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
