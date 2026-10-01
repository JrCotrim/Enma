using System.Data.Common;
using Enma.Application.Deadlines;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Queries;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDeadlineQueriesTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
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
    public async Task OwnershipLookups_WithTenantMatrix_BindResourceAndOrganizationPredicates()
    {
        DeadlineGraph graphA = CreateGraph("Organization A", "organization-a");
        DeadlineGraph graphB = CreateGraph("Organization B", "organization-b");
        await SeedAsync(graphA.Entities.Concat(graphB.Entities).ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateInterceptedContext(interceptor);
        var deadlineLookup = new DeadlineOrganizationOwnershipLookup(dbContext);
        var processLookup = new ProcessOrganizationOwnershipLookup(dbContext);

        Assert.True(await deadlineLookup.ExistsInOrganizationAsync(
            graphA.Deadline.Id,
            graphA.Organization.Id));
        Assert.False(await deadlineLookup.ExistsInOrganizationAsync(
            graphB.Deadline.Id,
            graphA.Organization.Id));
        Assert.False(await deadlineLookup.ExistsInOrganizationAsync(
            Guid.NewGuid(),
            graphA.Organization.Id));
        Assert.True(await processLookup.ExistsInOrganizationAsync(
            graphA.Process.Id,
            graphA.Organization.Id));
        Assert.False(await processLookup.ExistsInOrganizationAsync(
            graphB.Process.Id,
            graphA.Organization.Id));

        Assert.Equal(5, interceptor.ReaderCommandCount);
        Assert.All(
            interceptor.CommandTexts.Take(3),
            commandText =>
            {
                Assert.Contains("legal_deadlines", commandText);
                Assert.Contains("organization_id", commandText);
                Assert.Contains("id", commandText);
            });
        Assert.All(
            interceptor.CommandTexts.Skip(3),
            commandText =>
            {
                Assert.Contains("legal_processes", commandText);
                Assert.Contains("organization_id", commandText);
                Assert.Contains("id", commandText);
            });
    }

    [Fact]
    public async Task FindAsync_WithTenantBoundaryLifecycleAndInactiveClient_UsesOneProjectionQuery()
    {
        DeadlineGraph graphA = CreateGraph("Organization A", "organization-a");
        graphA.Client.Deactivate();
        DeadlineGraph graphB = CreateGraph("Organization B", "organization-b");
        var completedDeadline = new LegalDeadline(
            graphA.Organization.Id,
            graphA.Process.Id,
            "Completed Deadline",
            new DateOnly(2026, 9, 2),
            CreatedAt.AddMinutes(1));
        DateTimeOffset completedAt = CreatedAt.AddDays(1);
        completedDeadline.Complete(completedAt);
        await SeedAsync(
            graphA.Entities
                .Concat(graphB.Entities)
                .Append(completedDeadline)
                .ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateInterceptedContext(interceptor);
        var queries = new LegalDeadlineReadQueries(dbContext);

        LegalDeadlineDetailReadModel? pending = await queries.FindAsync(
            graphA.Deadline.Id,
            graphA.Organization.Id);

        Assert.NotNull(pending);
        Assert.Equal(graphA.Deadline.Id, pending.Id);
        Assert.Equal(graphA.Deadline.Title, pending.Title);
        Assert.Equal(graphA.Deadline.DueDate, pending.DueDate);
        Assert.Equal(graphA.Process.Id, pending.ProcessId);
        Assert.Equal(graphA.Process.Title, pending.ProcessTitle);
        Assert.Equal(graphA.Client.Name, pending.ClientName);
        Assert.Equal(LegalDeadlineReadState.Pending, pending.State);
        Assert.Equal(graphA.Deadline.CreatedAt, pending.CreatedAt);
        Assert.Null(pending.CompletedAt);
        Assert.Null(pending.ResponsibleMembershipId);
        Assert.Null(pending.ResponsibleDisplayName);
        Assert.Equal(1, interceptor.ReaderCommandCount);
        AssertProjectionSql(interceptor.LastCommandText, includesPagination: false);

        interceptor.Reset();
        LegalDeadlineDetailReadModel? completed = await queries.FindAsync(
            completedDeadline.Id,
            graphA.Organization.Id);

        Assert.Equal(LegalDeadlineReadState.Completed, completed?.State);
        Assert.Equal(completedAt, completed?.CompletedAt);
        Assert.Equal(1, interceptor.ReaderCommandCount);

        interceptor.Reset();
        LegalDeadlineDetailReadModel? crossTenant = await queries.FindAsync(
            graphB.Deadline.Id,
            graphA.Organization.Id);

        Assert.Null(crossTenant);
        Assert.Equal(1, interceptor.ReaderCommandCount);
    }

    [Fact]
    public async Task ListAsync_WithTenantLifecycleOrderingAndPagination_UsesOneBoundedQuery()
    {
        DeadlineGraph graphA = CreateGraph("Organization A", "organization-a");
        graphA.Client.Deactivate();
        DeadlineGraph graphB = CreateGraph("Organization B", "organization-b");
        var early = new LegalDeadline(
            graphA.Organization.Id,
            graphA.Process.Id,
            "Early",
            new DateOnly(2026, 8, 31),
            CreatedAt.AddMinutes(1));
        var tied = new LegalDeadline(
            graphA.Organization.Id,
            graphA.Process.Id,
            "Tied",
            graphA.Deadline.DueDate,
            CreatedAt.AddMinutes(2));
        tied.Complete(CreatedAt.AddDays(1));
        await SeedAsync(
            graphA.Entities
                .Concat(graphB.Entities)
                .Append(early)
                .Append(tied)
                .ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateInterceptedContext(interceptor);
        var queries = new LegalDeadlineReadQueries(dbContext);

        IReadOnlyList<LegalDeadlineListItem> firstPage = await queries.ListAsync(
            CreateListRequest(graphA.Organization.Id, 1, 3));

        Assert.Equal(3, firstPage.Count);
        Assert.Equal(early.Id, firstPage[0].Id);
        Assert.DoesNotContain(firstPage, item => item.Id == graphB.Deadline.Id);
        Assert.All(firstPage, item => Assert.Equal(graphA.Client.Name, item.ClientName));
        Assert.Contains(firstPage, item => item.State == LegalDeadlineReadState.Pending);
        Assert.Contains(firstPage, item => item.State == LegalDeadlineReadState.Completed);
        Assert.Equal(
            firstPage
                .OrderBy(item => item.DueDate)
                .ThenBy(item => item.Id.ToString(), StringComparer.Ordinal)
                .Select(item => item.Id),
            firstPage.Select(item => item.Id));
        Assert.Equal(1, interceptor.ReaderCommandCount);
        AssertProjectionSql(interceptor.LastCommandText, includesPagination: true);

        interceptor.Reset();
        IReadOnlyList<LegalDeadlineListItem> secondPage = await queries.ListAsync(
            CreateListRequest(graphA.Organization.Id, 2, 2));

        Assert.Single(secondPage);
        Assert.Equal(1, interceptor.ReaderCommandCount);
    }

    [Fact]
    public async Task ResponsibleReads_WithFilterInactiveAndCoincidentTenant_StayTenantScoped()
    {
        DeadlineGraph graphA = CreateGraph("Organization A", "organization-a");
        DeadlineGraph graphB = CreateGraph("Organization B", "organization-b");
        var activeUserA = new User(
            "Coincident Responsible",
            "coincident-responsible-a@example.test",
            CreatedAt);
        var inactiveUserA = new User(
            "Inactive Responsible",
            "inactive-responsible-a@example.test",
            CreatedAt);
        var userB = new User(
            "Coincident Responsible",
            "coincident-responsible-b@example.test",
            CreatedAt);
        var activeMembershipA = new OrganizationMembership(
            graphA.Organization.Id,
            activeUserA.Id,
            OrganizationRole.Member,
            CreatedAt);
        var inactiveMembershipA = new OrganizationMembership(
            graphA.Organization.Id,
            inactiveUserA.Id,
            OrganizationRole.Member,
            CreatedAt);
        inactiveMembershipA.Deactivate();
        var membershipB = new OrganizationMembership(
            graphB.Organization.Id,
            userB.Id,
            OrganizationRole.Member,
            CreatedAt);
        var assignedA = new LegalDeadline(
            graphA.Organization.Id,
            graphA.Process.Id,
            "Assigned",
            new DateOnly(2026, 9, 2),
            CreatedAt,
            activeMembershipA.Id);
        var inactiveAssignedA = new LegalDeadline(
            graphA.Organization.Id,
            graphA.Process.Id,
            "Inactive Assigned",
            new DateOnly(2026, 9, 3),
            CreatedAt,
            inactiveMembershipA.Id);
        var assignedB = new LegalDeadline(
            graphB.Organization.Id,
            graphB.Process.Id,
            "Assigned",
            new DateOnly(2026, 9, 2),
            CreatedAt,
            membershipB.Id);
        await SeedAsync(
            graphA.Entities
                .Concat(graphB.Entities)
                .Concat(
                [
                    activeUserA,
                    inactiveUserA,
                    userB,
                    activeMembershipA,
                    inactiveMembershipA,
                    membershipB,
                    assignedA,
                    inactiveAssignedA,
                    assignedB
                ])
                .ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateInterceptedContext(interceptor);
        var queries = new LegalDeadlineReadQueries(dbContext);

        IReadOnlyList<LegalDeadlineListItem> all = await queries.ListAsync(
            CreateListRequest(graphA.Organization.Id, 1, 20));

        (Guid Id, Guid? MembershipId, string? DisplayName)[] expected =
        [
            (graphA.Deadline.Id, null, null),
            (assignedA.Id, activeMembershipA.Id, "Coincident Responsible"),
            (inactiveAssignedA.Id, inactiveMembershipA.Id, "Inactive Responsible")
        ];
        Assert.Equal(
            expected,
            all.Select(item => (
                item.Id,
                item.ResponsibleMembershipId,
                item.ResponsibleDisplayName)));
        Assert.Equal(1, interceptor.ReaderCommandCount);
        AssertProjectionSql(interceptor.LastCommandText, includesPagination: true);
        Assert.Contains("organization_memberships", interceptor.LastCommandText);
        Assert.Contains("LEFT JOIN", interceptor.LastCommandText);

        async Task<Guid[]> ListIdsAsync(
            LegalDeadlineReadResponsibleFilterKind kind,
            Guid? membershipId)
        {
            interceptor.Reset();
            IReadOnlyList<LegalDeadlineListItem> items = await queries.ListAsync(
                new LegalDeadlineListReadRequest(
                    graphA.Organization.Id,
                    kind,
                    membershipId,
                    1,
                    20));
            Assert.Equal(1, interceptor.ReaderCommandCount);
            return items.Select(item => item.Id).ToArray();
        }

        Assert.Equal(
            [graphA.Deadline.Id],
            await ListIdsAsync(LegalDeadlineReadResponsibleFilterKind.Unassigned, null));
        Assert.Equal(
            [assignedA.Id],
            await ListIdsAsync(
                LegalDeadlineReadResponsibleFilterKind.Membership,
                activeMembershipA.Id));
        Assert.Equal(
            [inactiveAssignedA.Id],
            await ListIdsAsync(
                LegalDeadlineReadResponsibleFilterKind.Membership,
                inactiveMembershipA.Id));
        Assert.Empty(await ListIdsAsync(
            LegalDeadlineReadResponsibleFilterKind.Membership,
            membershipB.Id));
        await Assert.ThrowsAsync<ArgumentException>(
            () => queries.ListAsync(
                new LegalDeadlineListReadRequest(
                    graphA.Organization.Id,
                    LegalDeadlineReadResponsibleFilterKind.Membership,
                    null,
                    1,
                    20)));

        interceptor.Reset();
        LegalDeadlineDetailReadModel? inactiveDetail = await queries.FindAsync(
            inactiveAssignedA.Id,
            graphA.Organization.Id);
        LegalDeadlineDetailReadModel? unassignedDetail = await queries.FindAsync(
            graphA.Deadline.Id,
            graphA.Organization.Id);
        LegalDeadlineDetailReadModel? crossTenantDetail = await queries.FindAsync(
            assignedB.Id,
            graphA.Organization.Id);
        LegalDeadlineDetailReadModel? tenantBDetail = await queries.FindAsync(
            assignedB.Id,
            graphB.Organization.Id);

        Assert.Equal(inactiveMembershipA.Id, inactiveDetail?.ResponsibleMembershipId);
        Assert.Equal("Inactive Responsible", inactiveDetail?.ResponsibleDisplayName);
        Assert.NotNull(unassignedDetail);
        Assert.Null(unassignedDetail.ResponsibleMembershipId);
        Assert.Null(unassignedDetail.ResponsibleDisplayName);
        Assert.Null(crossTenantDetail);
        Assert.Equal(membershipB.Id, tenantBDetail?.ResponsibleMembershipId);
        Assert.Equal("Coincident Responsible", tenantBDetail?.ResponsibleDisplayName);
        Assert.Equal(4, interceptor.ReaderCommandCount);
    }

    private static LegalDeadlineListReadRequest CreateListRequest(
        Guid organizationId,
        int pageNumber,
        int pageSize)
    {
        return new LegalDeadlineListReadRequest(
            organizationId,
            LegalDeadlineReadResponsibleFilterKind.Any,
            null,
            pageNumber,
            pageSize);
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

    private static DeadlineGraph CreateGraph(
        string organizationName,
        string organizationSlug)
    {
        var organization = new Organization(
            organizationName,
            organizationSlug,
            CreatedAt);
        var client = new Client(
            organization.Id,
            $"{organizationName} Client",
            CreatedAt);
        var process = new LegalProcess(
            organization.Id,
            client.Id,
            $"{organizationName} Process",
            CreatedAt);
        var deadline = new LegalDeadline(
            organization.Id,
            process.Id,
            $"{organizationName} Deadline",
            new DateOnly(2026, 9, 1),
            CreatedAt);
        return new DeadlineGraph(organization, client, process, deadline);
    }

    private static void AssertProjectionSql(
        string commandText,
        bool includesPagination)
    {
        Assert.Contains("legal_deadlines", commandText);
        Assert.Contains("legal_processes", commandText);
        Assert.Contains("clients", commandText);
        Assert.Contains("INNER JOIN", commandText);
        Assert.Contains("organization_id", commandText);
        Assert.Contains("process_id", commandText);
        Assert.Contains("client_id", commandText);
        Assert.DoesNotContain("is_active", commandText);

        if (includesPagination)
        {
            Assert.Contains("ORDER BY", commandText);
            Assert.Contains("due_date", commandText);
            Assert.Contains("LIMIT", commandText);
            Assert.Contains("OFFSET", commandText);
        }
    }

    private sealed record DeadlineGraph(
        Organization Organization,
        Client Client,
        LegalProcess Process,
        LegalDeadline Deadline)
    {
        public object[] Entities =>
            [Organization, Client, Process, Deadline];
    }

    private sealed class ReaderCommandInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commandTexts = [];

        public int ReaderCommandCount => _commandTexts.Count;

        public IReadOnlyList<string> CommandTexts => _commandTexts;

        public string LastCommandText => _commandTexts.LastOrDefault() ?? string.Empty;

        public void Reset()
        {
            _commandTexts.Clear();
        }

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
