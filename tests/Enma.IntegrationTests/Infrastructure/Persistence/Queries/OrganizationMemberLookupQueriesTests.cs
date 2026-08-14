using System.Data.Common;
using Enma.Application.Organizations.Members.Lookup;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Queries;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationMemberLookupQueriesTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        14,
        18,
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
    public async Task SearchAsync_WithTenantActivityAndLiteralSearch_UsesOneBoundedProjectedQuery()
    {
        Organization organizationA = CreateOrganization(
            "Member Lookup A",
            "member-lookup-a");
        Organization organizationB = CreateOrganization(
            "Member Lookup B",
            "member-lookup-b");
        User caller = CreateUser("Caller Member", 1);
        User percentUser = CreateUser("Literal % Member", 2);
        User underscoreUser = CreateUser("Literal _ Member", 3);
        User backslashUser = CreateUser("Literal \\ Member", 4);
        User semicolonUser = CreateUser("Literal ; Member", 5);
        User caseUser = CreateUser("UPPERCASE MEMBER", 6);
        User inactiveMembershipUser = CreateUser("Inactive Membership", 7);
        User inactiveUser = CreateUser("Inactive User", 8);
        inactiveUser.Deactivate();
        User crossTenantUser = CreateUser("Cross Tenant Secret", 9);
        User dualUser = CreateUser("Dual Member", 10);
        OrganizationMembership callerMembership = CreateMembership(
            organizationA,
            caller,
            OrganizationRole.Member,
            1);
        OrganizationMembership percentMembership = CreateMembership(
            organizationA,
            percentUser,
            OrganizationRole.Owner,
            2);
        OrganizationMembership underscoreMembership = CreateMembership(
            organizationA,
            underscoreUser,
            OrganizationRole.Administrator,
            3);
        OrganizationMembership backslashMembership = CreateMembership(
            organizationA,
            backslashUser,
            OrganizationRole.Member,
            4);
        OrganizationMembership semicolonMembership = CreateMembership(
            organizationA,
            semicolonUser,
            OrganizationRole.Member,
            5);
        OrganizationMembership caseMembership = CreateMembership(
            organizationA,
            caseUser,
            OrganizationRole.Member,
            6);
        OrganizationMembership inactiveMembership = CreateMembership(
            organizationA,
            inactiveMembershipUser,
            OrganizationRole.Member,
            7);
        inactiveMembership.Deactivate();
        OrganizationMembership inactiveUserMembership = CreateMembership(
            organizationA,
            inactiveUser,
            OrganizationRole.Member,
            8);
        OrganizationMembership crossTenantMembership = CreateMembership(
            organizationB,
            crossTenantUser,
            OrganizationRole.Member,
            9);
        OrganizationMembership dualMembershipA = CreateMembership(
            organizationA,
            dualUser,
            OrganizationRole.Member,
            10);
        OrganizationMembership dualMembershipB = CreateMembership(
            organizationB,
            dualUser,
            OrganizationRole.Owner,
            11);
        await SeedAsync(
            [organizationA, organizationB],
            [
                caller,
                percentUser,
                underscoreUser,
                backslashUser,
                semicolonUser,
                caseUser,
                inactiveMembershipUser,
                inactiveUser,
                crossTenantUser,
                dualUser
            ],
            [
                callerMembership,
                percentMembership,
                underscoreMembership,
                backslashMembership,
                semicolonMembership,
                caseMembership,
                inactiveMembership,
                inactiveUserMembership,
                crossTenantMembership,
                dualMembershipA,
                dualMembershipB
            ]);
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateQueryContext(interceptor);
        var queries = new OrganizationMemberLookupQueries(dbContext);

        IReadOnlyList<OrganizationMemberLookupItem> allResults =
            await queries.SearchAsync(organizationA.Id, null, 1, 20);

        Assert.Contains(allResults, item => item.Id == callerMembership.Id);
        Assert.Contains(allResults, item => item.Id == dualMembershipA.Id);
        Assert.DoesNotContain(allResults, item => item.Id == dualMembershipB.Id);
        Assert.DoesNotContain(allResults, item => item.Id == inactiveMembership.Id);
        Assert.DoesNotContain(allResults, item => item.Id == inactiveUserMembership.Id);
        Assert.DoesNotContain(allResults, item => item.Id == crossTenantMembership.Id);
        Assert.Equal(1, interceptor.ReaderCommandCount);
        Assert.Contains("organization_id", interceptor.LastCommandText);
        Assert.Contains("is_active", interceptor.LastCommandText);
        Assert.Contains("INNER JOIN", interceptor.LastCommandText);
        Assert.Contains("ORDER BY", interceptor.LastCommandText);
        Assert.Contains("LIMIT", interceptor.LastCommandText);
        Assert.Contains("OFFSET", interceptor.LastCommandText);
        Assert.DoesNotContain("COUNT", interceptor.LastCommandText);
        Assert.DoesNotContain("email", interceptor.LastCommandText);
        Assert.DoesNotContain("created_at", interceptor.LastCommandText);

        await AssertSingleSearchAsync(
            queries,
            organizationA.Id,
            "%",
            percentMembership.Id);
        await AssertSingleSearchAsync(
            queries,
            organizationA.Id,
            "_",
            underscoreMembership.Id);
        await AssertSingleSearchAsync(
            queries,
            organizationA.Id,
            "\\",
            backslashMembership.Id);
        await AssertSingleSearchAsync(
            queries,
            organizationA.Id,
            ";",
            semicolonMembership.Id);
        await AssertSingleSearchAsync(
            queries,
            organizationA.Id,
            "uppercase",
            caseMembership.Id);

        IReadOnlyList<OrganizationMemberLookupItem> crossTenantSearch =
            await queries.SearchAsync(
                organizationA.Id,
                crossTenantUser.Name,
                1,
                20);
        IReadOnlyList<OrganizationMemberLookupItem> dualResultsB =
            await queries.SearchAsync(organizationB.Id, dualUser.Name, 1, 20);

        Assert.Empty(crossTenantSearch);
        Assert.Equal(dualMembershipB.Id, Assert.Single(dualResultsB).Id);
        Assert.Equal(8, interceptor.ReaderCommandCount);
    }

    [Fact]
    public async Task SearchAsync_WithMultiplePagesAndDuplicateNames_PreservesMembershipIdentityAndOrdering()
    {
        Organization organization = CreateOrganization(
            "Paged Members",
            "paged-members");
        User[] users = Enumerable.Range(1, 22)
            .Select(index => CreateUser($"Member {index:D2}", index))
            .ToArray();
        OrganizationMembership[] memberships = users
            .Select((user, index) => CreateMembership(
                organization,
                user,
                OrganizationRole.Member,
                index))
            .ToArray();
        User duplicateUserA = CreateUser("Same Name", 30);
        User duplicateUserB = CreateUser("Same Name", 31);
        OrganizationMembership duplicateMembershipA = CreateMembership(
            organization,
            duplicateUserA,
            OrganizationRole.Owner,
            30);
        OrganizationMembership duplicateMembershipB = CreateMembership(
            organization,
            duplicateUserB,
            OrganizationRole.Administrator,
            31);
        await SeedAsync(
            [organization],
            users.Append(duplicateUserA).Append(duplicateUserB).ToArray(),
            memberships
                .Append(duplicateMembershipA)
                .Append(duplicateMembershipB)
                .ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateQueryContext(interceptor);
        var queries = new OrganizationMemberLookupQueries(dbContext);

        IReadOnlyList<OrganizationMemberLookupItem> firstPageWithSentinel =
            await queries.SearchAsync(organization.Id, null, 1, 20);
        IReadOnlyList<OrganizationMemberLookupItem> secondPage =
            await queries.SearchAsync(organization.Id, null, 2, 20);
        IReadOnlyList<OrganizationMemberLookupItem> laterMemberSearch =
            await queries.SearchAsync(organization.Id, "Member 22", 1, 20);
        IReadOnlyList<OrganizationMemberLookupItem> duplicateResults =
            await queries.SearchAsync(organization.Id, "Same Name", 1, 20);

        Assert.Equal(21, firstPageWithSentinel.Count);
        Assert.Equal(
            new[]
            {
                memberships[20].Id,
                memberships[21].Id,
                duplicateMembershipA.Id,
                duplicateMembershipB.Id
            }
                .OrderBy(id => id),
            secondPage.Select(item => item.Id).OrderBy(id => id));
        Assert.Equal(memberships[21].Id, Assert.Single(laterMemberSearch).Id);
        Assert.Equal(
            new[] { duplicateMembershipA.Id, duplicateMembershipB.Id }
                .OrderBy(id => id),
            duplicateResults.Select(item => item.Id));
        Assert.Equal(4, interceptor.ReaderCommandCount);
    }

    private static async Task AssertSingleSearchAsync(
        OrganizationMemberLookupQueries queries,
        Guid organizationId,
        string search,
        Guid expectedMembershipId)
    {
        IReadOnlyList<OrganizationMemberLookupItem> results =
            await queries.SearchAsync(
                organizationId,
                search,
                1,
                20);

        Assert.Equal(expectedMembershipId, Assert.Single(results).Id);
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

    private async Task SeedAsync(
        IReadOnlyCollection<Organization> organizations,
        IReadOnlyCollection<User> users,
        IReadOnlyCollection<OrganizationMembership> memberships)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Organizations.AddRange(organizations);
        dbContext.Users.AddRange(users);
        dbContext.OrganizationMemberships.AddRange(memberships);
        await dbContext.SaveChangesAsync();
    }

    private static Organization CreateOrganization(string name, string slug)
    {
        return new Organization(name, slug, CreatedAt);
    }

    private static User CreateUser(string name, int marker)
    {
        return new User(name, $"member-{marker}@example.test", CreatedAt);
    }

    private static OrganizationMembership CreateMembership(
        Organization organization,
        User user,
        OrganizationRole role,
        int createdMinutesLater)
    {
        return new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            CreatedAt.AddMinutes(createdMinutesLater));
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
