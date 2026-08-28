using System.Data.Common;
using Enma.Application.Organizations.Members.List;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Queries;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationMemberAdministrationQueriesTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse(
        "2026-08-25T12:00:00Z");

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ListAsync_BasicActiveView_IsEffectiveActiveNameOnlyAndTenantSafe()
    {
        TestGraph graph = CreateGraph();
        await SeedAsync(graph.Entities);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new OrganizationMemberAdministrationQueries(dbContext);

        OrganizationMemberAdministrationPage page = await queries.ListAsync(
            CreateQuery(
                graph.Organization.Id,
                OrganizationMembershipStatus.Active,
                detailLevel: OrganizationMemberDetailLevel.Basic,
                pageSize: 100));
        OrganizationMemberAdministrationPage emailSearch = await queries.ListAsync(
            CreateQuery(
                graph.Organization.Id,
                OrganizationMembershipStatus.Active,
                search: "privileged.search@example.test",
                detailLevel: OrganizationMemberDetailLevel.Basic));
        OrganizationMemberAdministrationPage nameSearch = await queries.ListAsync(
            CreateQuery(
                graph.Organization.Id,
                OrganizationMembershipStatus.Active,
                search: "same name",
                detailLevel: OrganizationMemberDetailLevel.Basic));

        Guid[] expectedIds = graph.Members
            .Where(member => member.Membership.IsActive && member.User.IsActive)
            .Select(member => member.Membership.Id)
            .OrderBy(id => id)
            .ToArray();
        Assert.Equal(expectedIds.Length, page.TotalCount);
        Assert.Equal(
            expectedIds,
            page.Items.Select(item => item.Id).OrderBy(id => id));
        Assert.DoesNotContain(
            page.Items,
            item => item.Id == graph.ActiveMembershipInactiveUser.Membership.Id ||
                item.Id == graph.InactiveMembershipActiveUser.Membership.Id ||
                item.Id == graph.InactiveMembershipInactiveUser.Membership.Id ||
                item.Id == graph.ForeignMember.Membership.Id);
        Assert.All(
            page.Items,
            item =>
            {
                Assert.Null(item.Email);
                Assert.Null(item.MembershipStatus);
                Assert.Null(item.AccountStatus);
            });
        Assert.Equal(0, emailSearch.TotalCount);
        Assert.Empty(emailSearch.Items);
        Assert.Equal(2, nameSearch.TotalCount);
        Assert.All(nameSearch.Items, item => Assert.Equal("Same Name", item.Name));
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ListAsync_AdministrativeViews_SeparateMembershipAndAccountStatus()
    {
        TestGraph graph = CreateGraph();
        await SeedAsync(graph.Entities);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new OrganizationMemberAdministrationQueries(dbContext);

        OrganizationMemberAdministrationPage active = await queries.ListAsync(
            CreateQuery(
                graph.Organization.Id,
                OrganizationMembershipStatus.Active,
                detailLevel: OrganizationMemberDetailLevel.Administrative,
                pageSize: 100));
        OrganizationMemberAdministrationPage inactive = await queries.ListAsync(
            CreateQuery(
                graph.Organization.Id,
                OrganizationMembershipStatus.Inactive,
                detailLevel: OrganizationMemberDetailLevel.Administrative,
                pageSize: 100));

        Assert.Equal(
            graph.Members.Count(member => member.Membership.IsActive),
            active.TotalCount);
        OrganizationMemberAdministrationReadModel inactiveAccount = Assert.Single(
            active.Items,
            item => item.Id == graph.ActiveMembershipInactiveUser.Membership.Id);
        Assert.Equal(
            OrganizationMembershipStatus.Active,
            inactiveAccount.MembershipStatus);
        Assert.Equal(OrganizationAccountStatus.Inactive, inactiveAccount.AccountStatus);
        Assert.Equal(
            graph.ActiveMembershipInactiveUser.User.Email,
            inactiveAccount.Email);

        Assert.Equal(2, inactive.TotalCount);
        OrganizationMemberAdministrationReadModel activeAccount = Assert.Single(
            inactive.Items,
            item => item.Id == graph.InactiveMembershipActiveUser.Membership.Id);
        OrganizationMemberAdministrationReadModel bothInactive = Assert.Single(
            inactive.Items,
            item => item.Id == graph.InactiveMembershipInactiveUser.Membership.Id);
        Assert.Equal(
            OrganizationMembershipStatus.Inactive,
            activeAccount.MembershipStatus);
        Assert.Equal(OrganizationAccountStatus.Active, activeAccount.AccountStatus);
        Assert.Equal(
            OrganizationMembershipStatus.Inactive,
            bothInactive.MembershipStatus);
        Assert.Equal(OrganizationAccountStatus.Inactive, bothInactive.AccountStatus);
        Assert.DoesNotContain(
            active.Items.Concat(inactive.Items),
            item => item.Id == graph.ForeignMember.Membership.Id);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ListAsync_AdministrativeSearch_MatchesNameOrNormalizedEmailOnlyInTenant()
    {
        TestGraph graph = CreateGraph();
        await SeedAsync(graph.Entities);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new OrganizationMemberAdministrationQueries(dbContext);

        OrganizationMemberAdministrationPage emailSearch = await queries.ListAsync(
            CreateQuery(
                graph.Organization.Id,
                OrganizationMembershipStatus.Active,
                search: "PRIVILEGED.SEARCH@EXAMPLE.TEST",
                detailLevel: OrganizationMemberDetailLevel.Administrative));
        OrganizationMemberAdministrationPage escapedNameSearch =
            await queries.ListAsync(CreateQuery(
                graph.Organization.Id,
                OrganizationMembershipStatus.Active,
                search: "literal %_\\ member",
                detailLevel: OrganizationMemberDetailLevel.Administrative));
        OrganizationMemberAdministrationPage foreignSearch = await queries.ListAsync(
            CreateQuery(
                graph.Organization.Id,
                OrganizationMembershipStatus.Active,
                search: "foreign-only",
                detailLevel: OrganizationMemberDetailLevel.Administrative));

        Assert.Equal(
            graph.PrivilegedSearchMember.Membership.Id,
            Assert.Single(emailSearch.Items).Id);
        Assert.Equal(1, emailSearch.TotalCount);
        Assert.Equal(
            graph.LiteralNameMember.Membership.Id,
            Assert.Single(escapedNameSearch.Items).Id);
        Assert.Equal(1, escapedNameSearch.TotalCount);
        Assert.Empty(foreignSearch.Items);
        Assert.Equal(0, foreignSearch.TotalCount);
    }

    [Fact]
    public async Task ListAsync_PaginatesDeterministicallyWithTwoTenantQualifiedCommands()
    {
        TestGraph graph = CreateGraph();
        await SeedAsync(graph.Entities);
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateContext(interceptor);
        var queries = new OrganizationMemberAdministrationQueries(dbContext);

        OrganizationMemberAdministrationPage page = await queries.ListAsync(
            CreateQuery(
                graph.Organization.Id,
                OrganizationMembershipStatus.Active,
                pageNumber: 2,
                pageSize: 2,
                detailLevel: OrganizationMemberDetailLevel.Basic));

        OrganizationMember[] visibleOrdered = graph.Members
            .Where(member => member.Membership.IsActive && member.User.IsActive)
            .OrderBy(member => member.User.Name)
            .ThenBy(member => member.Membership.Id)
            .ToArray();
        Assert.Equal(visibleOrdered.Length, page.TotalCount);
        Assert.Equal(
            visibleOrdered.Skip(2).Take(2).Select(member => member.Membership.Id),
            page.Items.Select(item => item.Id));
        Assert.Equal(2, interceptor.CommandTexts.Count);
        Assert.All(
            interceptor.CommandTexts,
            commandText =>
            {
                Assert.Contains("organization_memberships", commandText);
                Assert.Contains("organization_id", commandText);
                Assert.Contains("WHERE", commandText);
            });
        Assert.Contains("count", interceptor.CommandTexts[0],
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", interceptor.CommandTexts[1]);
        Assert.Contains("LIMIT", interceptor.CommandTexts[1]);
        Assert.Contains("OFFSET", interceptor.CommandTexts[1]);
        Assert.DoesNotContain("email", interceptor.CommandTexts[1],
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    private OrganizationMemberAdministrationQuery CreateQuery(
        Guid organizationId,
        OrganizationMembershipStatus membershipStatus,
        string? search = null,
        int pageNumber = 1,
        int pageSize = 20,
        OrganizationMemberDetailLevel detailLevel =
            OrganizationMemberDetailLevel.Basic)
    {
        return new OrganizationMemberAdministrationQuery(
            organizationId,
            membershipStatus,
            search,
            pageNumber,
            pageSize,
            detailLevel);
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

    private async Task SeedAsync(IReadOnlyCollection<object> entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static TestGraph CreateGraph()
    {
        var organization = new Organization(
            "Current Legal",
            $"current-{Guid.NewGuid():N}",
            CreatedAt);
        var foreignOrganization = new Organization(
            "Foreign Legal",
            $"foreign-{Guid.NewGuid():N}",
            CreatedAt);
        OrganizationMember activeMember = CreateMember(
            organization,
            "Alpha Active",
            "alpha.active@example.test",
            OrganizationRole.Owner);
        OrganizationMember sameNameOne = CreateMember(
            organization,
            "Same Name",
            "same.one@example.test",
            OrganizationRole.Member);
        OrganizationMember sameNameTwo = CreateMember(
            organization,
            "Same Name",
            "same.two@example.test",
            OrganizationRole.Administrator);
        OrganizationMember privilegedSearchMember = CreateMember(
            organization,
            "Privileged Search",
            "privileged.search@example.test",
            OrganizationRole.Member);
        OrganizationMember literalNameMember = CreateMember(
            organization,
            "Literal %_\\ Member",
            "literal@example.test",
            OrganizationRole.Member);
        OrganizationMember activeMembershipInactiveUser = CreateMember(
            organization,
            "Inactive Account",
            "inactive.account@example.test",
            OrganizationRole.Member,
            activeUser: false);
        OrganizationMember inactiveMembershipActiveUser = CreateMember(
            organization,
            "Inactive Membership Active Account",
            "inactive.membership.active@example.test",
            OrganizationRole.Member,
            activeMembership: false);
        OrganizationMember inactiveMembershipInactiveUser = CreateMember(
            organization,
            "Both Inactive",
            "both.inactive@example.test",
            OrganizationRole.Member,
            activeMembership: false,
            activeUser: false);
        OrganizationMember foreignMember = CreateMember(
            foreignOrganization,
            "Alpha Active",
            "foreign-only@example.test",
            OrganizationRole.Owner);
        OrganizationMember[] members =
        [
            activeMember,
            sameNameOne,
            sameNameTwo,
            privilegedSearchMember,
            literalNameMember,
            activeMembershipInactiveUser,
            inactiveMembershipActiveUser,
            inactiveMembershipInactiveUser
        ];
        object[] entities =
        [
            organization,
            foreignOrganization,
            .. members.SelectMany(member => new object[]
            {
                member.User,
                member.Membership
            }),
            foreignMember.User,
            foreignMember.Membership
        ];

        return new TestGraph(
            organization,
            members,
            activeMembershipInactiveUser,
            inactiveMembershipActiveUser,
            inactiveMembershipInactiveUser,
            privilegedSearchMember,
            literalNameMember,
            foreignMember,
            entities);
    }

    private static OrganizationMember CreateMember(
        Organization organization,
        string name,
        string email,
        OrganizationRole role,
        bool activeMembership = true,
        bool activeUser = true)
    {
        var user = new User(name, email, CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            CreatedAt);

        if (!activeUser)
        {
            user.Deactivate();
        }

        if (!activeMembership)
        {
            membership.Deactivate();
        }

        return new OrganizationMember(user, membership);
    }

    private sealed record OrganizationMember(
        User User,
        OrganizationMembership Membership);

    private sealed record TestGraph(
        Organization Organization,
        IReadOnlyList<OrganizationMember> Members,
        OrganizationMember ActiveMembershipInactiveUser,
        OrganizationMember InactiveMembershipActiveUser,
        OrganizationMember InactiveMembershipInactiveUser,
        OrganizationMember PrivilegedSearchMember,
        OrganizationMember LiteralNameMember,
        OrganizationMember ForeignMember,
        IReadOnlyCollection<object> Entities);

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
