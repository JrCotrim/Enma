using System.Data.Common;
using Enma.Application.Processes;
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
public sealed class LegalProcessQueriesTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        13,
        16,
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
    public async Task ActiveClientLookup_WithTenantAndActivityMatrix_BindsAllPredicates()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "organization-b");
        var activeClientA = new Client(
            organizationA.Id,
            "Active Client A",
            CreatedAt);
        var inactiveClientA = new Client(
            organizationA.Id,
            "Inactive Client A",
            CreatedAt);
        inactiveClientA.Deactivate();
        var activeClientB = new Client(
            organizationB.Id,
            "Active Client B",
            CreatedAt);
        await SeedAsync(
            organizationA,
            organizationB,
            activeClientA,
            inactiveClientA,
            activeClientB);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var lookup = new ActiveClientInOrganizationLookup(dbContext);

        bool activeSameTenant = await lookup.ExistsAsync(
            activeClientA.Id,
            organizationA.Id);
        bool inactiveSameTenant = await lookup.ExistsAsync(
            inactiveClientA.Id,
            organizationA.Id);
        bool activeCrossTenant = await lookup.ExistsAsync(
            activeClientB.Id,
            organizationA.Id);
        bool missing = await lookup.ExistsAsync(
            Guid.NewGuid(),
            organizationA.Id);

        Assert.True(activeSameTenant);
        Assert.False(inactiveSameTenant);
        Assert.False(activeCrossTenant);
        Assert.False(missing);
    }

    [Fact]
    public async Task FindAsync_WithTenantBoundaryAndInactiveClient_ProjectsApprovedFields()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "organization-b");
        var clientA = new Client(organizationA.Id, "Client A", CreatedAt);
        clientA.Deactivate();
        var clientB = new Client(organizationB.Id, "Client B", CreatedAt);
        var processA = new LegalProcess(
            organizationA.Id,
            clientA.Id,
            "Process A",
            CreatedAt.AddMinutes(1));
        var processB = new LegalProcess(
            organizationB.Id,
            clientB.Id,
            "Process B",
            CreatedAt.AddMinutes(2));
        await SeedAsync(
            organizationA,
            organizationB,
            clientA,
            clientB,
            processA,
            processB);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new LegalProcessReadQueries(dbContext);

        LegalProcessReadModel? sameTenant = await queries.FindAsync(
            processA.Id,
            organizationA.Id);
        LegalProcessReadModel? crossTenant = await queries.FindAsync(
            processB.Id,
            organizationA.Id);
        LegalProcessReadModel? changedContext = await queries.FindAsync(
            processB.Id,
            organizationB.Id);

        Assert.Equal(
            new LegalProcessReadModel(
                processA.Id,
                processA.Title,
                clientA.Id,
                clientA.Name,
                processA.CreatedAt,
                null,
                LegalProcessStatus.InProgress,
                null,
                null,
                null),
            sameTenant);
        Assert.Null(crossTenant);
        Assert.Equal(processB.Id, changedContext?.Id);
        Assert.Equal(clientB.Name, changedContext?.ClientName);
    }

    [Fact]
    public async Task ListAsync_WithTenantPaginationAndInactiveClient_UsesOneBoundedOrderedQuery()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "organization-b");
        var clientA = new Client(organizationA.Id, "Client A", CreatedAt);
        clientA.Deactivate();
        var clientB = new Client(organizationB.Id, "Client B", CreatedAt);
        var zetaA = new LegalProcess(
            organizationA.Id,
            clientA.Id,
            "Zeta",
            CreatedAt);
        var alphaA1 = new LegalProcess(
            organizationA.Id,
            clientA.Id,
            "Alpha",
            CreatedAt.AddMinutes(1));
        var alphaA2 = new LegalProcess(
            organizationA.Id,
            clientA.Id,
            "Alpha",
            CreatedAt.AddMinutes(2));
        var crossTenant = new LegalProcess(
            organizationB.Id,
            clientB.Id,
            "Aardvark",
            CreatedAt);
        await SeedAsync(
            organizationA,
            organizationB,
            clientA,
            clientB,
            zetaA,
            alphaA1,
            alphaA2,
            crossTenant);
        var interceptor = new ReaderCommandInterceptor();
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;
        await using var dbContext = new EnmaDbContext(options);
        var queries = new LegalProcessReadQueries(dbContext);

        IReadOnlyList<LegalProcessReadModel> firstPageWithSentinel =
            await queries.ListAsync(CreateListRequest(
                organizationA.Id,
                pageNumber: 1,
                pageSize: 2));

        LegalProcess[] expectedFirstPage = new[] { alphaA1, alphaA2 }
            .OrderBy(legalProcess => legalProcess.Id)
            .Append(zetaA)
            .ToArray();
        Assert.Equal(
            expectedFirstPage.Select(legalProcess => legalProcess.Id),
            firstPageWithSentinel.Select(legalProcess => legalProcess.Id));
        Assert.All(
            firstPageWithSentinel,
            item => Assert.Equal(clientA.Name, item.ClientName));
        Assert.DoesNotContain(
            firstPageWithSentinel,
            item => item.Id == crossTenant.Id);
        Assert.Equal(1, interceptor.ReaderCommandCount);
        Assert.Contains("INNER JOIN", interceptor.LastCommandText);
        Assert.Contains("ORDER BY", interceptor.LastCommandText);
        Assert.Contains("LIMIT", interceptor.LastCommandText);
        Assert.Contains("OFFSET", interceptor.LastCommandText);

        IReadOnlyList<LegalProcessReadModel> secondPage = await queries.ListAsync(
            CreateListRequest(organizationA.Id, pageNumber: 2, pageSize: 2));

        LegalProcessReadModel item = Assert.Single(secondPage);
        Assert.Equal(zetaA.Id, item.Id);
        Assert.Equal(clientA.Name, item.ClientName);
        Assert.DoesNotContain("is_active", interceptor.LastCommandText);
    }

    [Fact]
    public async Task ListAsync_WithSearch_MatchesTitleClientNumberAndDigitsWithoutTenantLeak()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "organization-b");
        var plainClientA = new Client(organizationA.Id, "Plain Client", CreatedAt);
        var namedClientA = new Client(organizationA.Id, "Acme Holdings", CreatedAt);
        var namedClientB = new Client(organizationB.Id, "Acme Holdings", CreatedAt);
        var titleProcess = new LegalProcess(
            organizationA.Id,
            plainClientA.Id,
            "Recurso Especial",
            CreatedAt);
        var clientProcess = new LegalProcess(
            organizationA.Id,
            namedClientA.Id,
            "Unrelated Title",
            CreatedAt);
        var cnjProcess = new LegalProcess(
            organizationA.Id,
            plainClientA.Id,
            "Numbered",
            CreatedAt,
            "0001234-56.2026.8.19.0001");
        var freeProcess = new LegalProcess(
            organizationA.Id,
            plainClientA.Id,
            "Free Identifier",
            CreatedAt,
            "Proc. ABC/77");
        var percentProcess = new LegalProcess(
            organizationA.Id,
            plainClientA.Id,
            "Literal % Process",
            CreatedAt);
        var underscoreProcess = new LegalProcess(
            organizationA.Id,
            plainClientA.Id,
            "Literal _ Process",
            CreatedAt);
        var crossTenantProcess = new LegalProcess(
            organizationB.Id,
            namedClientB.Id,
            "Recurso Especial",
            CreatedAt,
            "0001234-56.2026.8.19.0001");
        await SeedAsync(
            organizationA,
            organizationB,
            plainClientA,
            namedClientA,
            namedClientB,
            titleProcess,
            clientProcess,
            cnjProcess,
            freeProcess,
            percentProcess,
            underscoreProcess,
            crossTenantProcess);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new LegalProcessReadQueries(dbContext);

        async Task<Guid[]> SearchAsync(string search)
        {
            IReadOnlyList<LegalProcessReadModel> items = await queries.ListAsync(
                CreateListRequest(organizationA.Id, search: search));
            return items.Select(item => item.Id).ToArray();
        }

        Assert.Equal([titleProcess.Id], await SearchAsync("recurso"));
        Assert.Equal([clientProcess.Id], await SearchAsync("ACME"));
        Assert.Equal([cnjProcess.Id], await SearchAsync("0001234-56.2026.8.19.0001"));
        Assert.Equal([cnjProcess.Id], await SearchAsync("00012345620268190001"));
        Assert.Equal([cnjProcess.Id], await SearchAsync("0001234-56"));
        Assert.Equal([cnjProcess.Id], await SearchAsync("620268"));
        Assert.Equal([freeProcess.Id], await SearchAsync("abc/77"));
        Assert.Equal([percentProcess.Id], await SearchAsync("%"));
        Assert.Equal([underscoreProcess.Id], await SearchAsync("_"));
        Assert.Empty(await SearchAsync("0009999"));
        Assert.Empty(await SearchAsync("\\"));
    }

    [Fact]
    public async Task ListAsync_WithStatusResponsibleAndSort_FiltersAndOrdersWithinTenant()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "organization-b");
        var firstUser = new User(
            "Responsible One",
            "responsible-one@example.test",
            CreatedAt);
        var secondUser = new User(
            "Responsible Two",
            "responsible-two@example.test",
            CreatedAt);
        var otherUser = new User(
            "Responsible Other",
            "responsible-other@example.test",
            CreatedAt);
        var firstMembership = new OrganizationMembership(
            organizationA.Id,
            firstUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var secondMembership = new OrganizationMembership(
            organizationA.Id,
            secondUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        var otherMembership = new OrganizationMembership(
            organizationB.Id,
            otherUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var clientA = new Client(organizationA.Id, "Client A", CreatedAt);
        var clientB = new Client(organizationB.Id, "Client B", CreatedAt);
        LegalProcess alpha = CreateProcess(
            organizationA,
            clientA,
            "Alpha",
            1,
            LegalProcessStatus.InProgress,
            firstMembership.Id);
        LegalProcess bravo = CreateProcess(
            organizationA,
            clientA,
            "Bravo",
            2,
            LegalProcessStatus.Suspended,
            secondMembership.Id);
        LegalProcess charlie = CreateProcess(
            organizationA,
            clientA,
            "Charlie",
            3,
            LegalProcessStatus.Closed,
            null);
        LegalProcess delta = CreateProcess(
            organizationA,
            clientA,
            "Delta",
            4,
            LegalProcessStatus.InProgress,
            null);
        LegalProcess echo = CreateProcess(
            organizationA,
            clientA,
            "Echo",
            5,
            LegalProcessStatus.InProgress,
            null);
        LegalProcess foxtrot = CreateProcess(
            organizationA,
            clientA,
            "Foxtrot",
            5,
            LegalProcessStatus.InProgress,
            null);
        LegalProcess crossTenant = CreateProcess(
            organizationB,
            clientB,
            "Alpha",
            1,
            LegalProcessStatus.InProgress,
            otherMembership.Id);
        await SeedAsync(
            organizationA,
            organizationB,
            firstUser,
            secondUser,
            otherUser,
            firstMembership,
            secondMembership,
            otherMembership,
            clientA,
            clientB,
            alpha,
            bravo,
            charlie,
            delta,
            echo,
            foxtrot,
            crossTenant);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new LegalProcessReadQueries(dbContext);
        Guid[] newestTie = new[] { echo.Id, foxtrot.Id }
            .OrderByDescending(id => id)
            .ToArray();

        async Task<Guid[]> ListIdsAsync(LegalProcessListReadRequest request)
        {
            IReadOnlyList<LegalProcessReadModel> items =
                await queries.ListAsync(request);
            return items.Select(item => item.Id).ToArray();
        }

        Assert.Equal(
            [alpha.Id, delta.Id, echo.Id, foxtrot.Id],
            await ListIdsAsync(CreateListRequest(
                organizationA.Id,
                status: LegalProcessStatus.InProgress)));
        Assert.Equal(
            [bravo.Id],
            await ListIdsAsync(CreateListRequest(
                organizationA.Id,
                status: LegalProcessStatus.Suspended)));
        Assert.Equal(
            [charlie.Id],
            await ListIdsAsync(CreateListRequest(
                organizationA.Id,
                status: LegalProcessStatus.Closed)));
        Assert.Equal(
            [charlie.Id, delta.Id, echo.Id, foxtrot.Id],
            await ListIdsAsync(CreateListRequest(
                organizationA.Id,
                responsibleFilterKind: LegalProcessReadResponsibleFilterKind.Unassigned)));
        Assert.Equal(
            [bravo.Id],
            await ListIdsAsync(CreateListRequest(
                organizationA.Id,
                responsibleFilterKind: LegalProcessReadResponsibleFilterKind.Membership,
                responsibleMembershipId: secondMembership.Id)));
        Assert.Empty(
            await ListIdsAsync(CreateListRequest(
                organizationA.Id,
                responsibleFilterKind: LegalProcessReadResponsibleFilterKind.Membership,
                responsibleMembershipId: otherMembership.Id)));
        Assert.Equal(
            newestTie.Concat([delta.Id, charlie.Id, bravo.Id, alpha.Id]),
            await ListIdsAsync(CreateListRequest(
                organizationA.Id,
                sort: LegalProcessListSort.Newest)));
        Assert.Equal(
            newestTie.Append(delta.Id),
            await ListIdsAsync(CreateListRequest(
                organizationA.Id,
                status: LegalProcessStatus.InProgress,
                responsibleFilterKind: LegalProcessReadResponsibleFilterKind.Unassigned,
                sort: LegalProcessListSort.Newest)));

        LegalProcessReadModel assigned = Assert.Single(await queries.ListAsync(
            CreateListRequest(
                organizationA.Id,
                responsibleFilterKind: LegalProcessReadResponsibleFilterKind.Membership,
                responsibleMembershipId: firstMembership.Id)));
        Assert.Equal(alpha.Id, assigned.Id);
        Assert.Equal(firstMembership.Id, assigned.ResponsibleMembershipId);
        Assert.Equal(firstUser.Name, assigned.ResponsibleDisplayName);

        Assert.Equal(
            6,
            (await queries.ListAsync(CreateListRequest(
                organizationA.Id,
                pageSize: 6))).Count);
        Assert.Equal(
            6,
            (await queries.ListAsync(CreateListRequest(
                organizationA.Id,
                pageSize: 5))).Count);
        Assert.Equal(
            [foxtrot.Id],
            await ListIdsAsync(CreateListRequest(
                organizationA.Id,
                pageNumber: 2,
                pageSize: 5)));
        Assert.Empty(
            await ListIdsAsync(CreateListRequest(
                organizationA.Id,
                pageNumber: int.MaxValue,
                pageSize: 100)));
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
        int createdMinutesAfter,
        LegalProcessStatus status,
        Guid? responsibleMembershipId)
    {
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            title,
            CreatedAt.AddMinutes(createdMinutesAfter),
            responsibleMembershipId: responsibleMembershipId);
        legalProcess.ChangeStatus(status);
        return legalProcess;
    }

    private static LegalProcessListReadRequest CreateListRequest(
        Guid organizationId,
        string? search = null,
        LegalProcessStatus? status = null,
        LegalProcessReadResponsibleFilterKind responsibleFilterKind =
            LegalProcessReadResponsibleFilterKind.Any,
        Guid? responsibleMembershipId = null,
        LegalProcessListSort sort = LegalProcessListSort.Title,
        int pageNumber = 1,
        int pageSize = 20)
    {
        return new LegalProcessListReadRequest(
            organizationId,
            search,
            status,
            responsibleFilterKind,
            responsibleMembershipId,
            sort,
            pageNumber,
            pageSize);
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
