using Enma.Application.Authorization;
using Enma.Application.Processes.Create;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Application.Processes;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalProcessCreationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string CnjFormatted = "0001234-56.2026.8.19.0001";
    private const string CnjDigits = "00012345620268190001";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        9,
        29,
        12,
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
    public async Task Create_WithAllOperationalFields_PersistsAndAuditsOnlyCreation()
    {
        TestGraph graph = await SeedGraphAsync();

        CreateLegalProcessResult result = await CreateAsync(
            graph,
            "  Full Process  ",
            $"  {CnjFormatted}  ",
            "suspended",
            "  1ª Vara Cível  ",
            graph.ResponsibleMembership.Id);

        Assert.Equal(CreateLegalProcessResultStatus.Succeeded, result.Status);
        LegalProcess persisted = await GetProcessAsync(AssertProcessId(result));
        Assert.Equal(graph.OrganizationA.Id, persisted.OrganizationId);
        Assert.Equal(graph.ClientA.Id, persisted.ClientId);
        Assert.Equal("Full Process", persisted.Title);
        Assert.Equal(CnjFormatted, persisted.ProcessNumber);
        Assert.Equal(CnjDigits, persisted.NormalizedProcessNumber);
        Assert.Equal(LegalProcessStatus.Suspended, persisted.Status);
        Assert.Equal("1ª Vara Cível", persisted.CourtOrAuthority);
        Assert.Equal(graph.ResponsibleMembership.Id, persisted.ResponsibleMembershipId);

        AuditLog auditLog = Assert.Single(await GetAuditLogsAsync());
        Assert.Equal(AuditEventType.LegalProcessCreated, auditLog.EventType);
        Assert.Equal(AuditEntityType.LegalProcess, auditLog.EntityType);
        Assert.Equal(persisted.Id, auditLog.EntityId);
        Assert.Equal(graph.OwnerMembership.Id, auditLog.ActorMembershipId);
        Assert.Null(auditLog.Details);
        Assert.Null(Assert.Single(await GetRawAuditDetailsAsync()));
    }

    [Theory]
    [InlineData(null, LegalProcessStatus.InProgress)]
    [InlineData("inProgress", LegalProcessStatus.InProgress)]
    [InlineData("suspended", LegalProcessStatus.Suspended)]
    [InlineData("closed", LegalProcessStatus.Closed)]
    public async Task Create_WithEachInitialStatus_PersistsStatusAndResponsible(
        string? status,
        LegalProcessStatus expected)
    {
        TestGraph graph = await SeedGraphAsync();

        CreateLegalProcessResult result = await CreateAsync(
            graph,
            "Status Process",
            status: status,
            responsibleMembershipId: graph.ResponsibleMembership.Id);

        LegalProcess persisted = await GetProcessAsync(AssertProcessId(result));
        Assert.Equal(expected, persisted.Status);
        Assert.Equal(graph.ResponsibleMembership.Id, persisted.ResponsibleMembershipId);
        Assert.Equal(
            [AuditEventType.LegalProcessCreated],
            (await GetAuditLogsAsync()).Select(auditLog => auditLog.EventType));
    }

    [Fact]
    public async Task Create_WithOnlyClientAndTitle_UsesOperationalDefaults()
    {
        TestGraph graph = await SeedGraphAsync();

        CreateLegalProcessResult result = await CreateAsync(graph, "Legacy Request");

        LegalProcess persisted = await GetProcessAsync(AssertProcessId(result));
        Assert.Equal("Legacy Request", persisted.Title);
        Assert.Null(persisted.ProcessNumber);
        Assert.Null(persisted.NormalizedProcessNumber);
        Assert.Equal(LegalProcessStatus.InProgress, persisted.Status);
        Assert.Null(persisted.CourtOrAuthority);
        Assert.Null(persisted.ResponsibleMembershipId);
    }

    [Fact]
    public async Task Create_WithDuplicateNumberInTenant_ConflictsWithoutPersistenceOrAudit()
    {
        TestGraph graph = await SeedGraphAsync();

        CreateLegalProcessResult first = await CreateAsync(
            graph,
            "First Number",
            CnjFormatted);
        CreateLegalProcessResult duplicate = await CreateAsync(
            graph,
            "Duplicate Number",
            CnjDigits,
            "closed",
            "Should Not Persist",
            graph.ResponsibleMembership.Id);
        CreateLegalProcessResult otherTenant = await CreateAsync(
            graph.OtherUser.Id,
            graph.OrganizationB.Id,
            graph.ClientB.Id,
            "Other Tenant Number",
            CnjFormatted);

        Assert.Equal(CreateLegalProcessResultStatus.Succeeded, first.Status);
        Assert.Same(CreateLegalProcessResult.DuplicateProcessNumber, duplicate);
        Assert.Equal(CreateLegalProcessResultStatus.Succeeded, otherTenant.Status);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.False(await dbContext.LegalProcesses.AnyAsync(
            legalProcess => legalProcess.Title == "Duplicate Number"));
        Assert.Equal(
            2,
            await dbContext.LegalProcesses.CountAsync(
                legalProcess => legalProcess.NormalizedProcessNumber == CnjDigits));
        Assert.Equal(2, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Create_ConcurrentSameNumber_ExactlyOneSucceedsAndOtherConflicts()
    {
        TestGraph graph = await SeedGraphAsync();

        CreateLegalProcessResult[] results = await Task.WhenAll(
            Task.Run(() => CreateAsync(graph, "Concurrent One", "ABC-1")),
            Task.Run(() => CreateAsync(graph, "Concurrent Two", "abc-1")));

        Assert.Single(
            results,
            result => result.Status == CreateLegalProcessResultStatus.Succeeded);
        Assert.Single(
            results,
            result => result.Status ==
                CreateLegalProcessResultStatus.DuplicateProcessNumber);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(
            1,
            await dbContext.LegalProcesses.CountAsync(
                legalProcess => legalProcess.NormalizedProcessNumber == "ABC-1"));
        Assert.Equal(
            1,
            await dbContext.AuditLogs.CountAsync(auditLog =>
                auditLog.EventType == AuditEventType.LegalProcessCreated));
    }

    [Theory]
    [InlineData(UnavailableResponsible.OtherTenant, "inProgress")]
    [InlineData(UnavailableResponsible.Missing, "inProgress")]
    [InlineData(UnavailableResponsible.InactiveMembership, "inProgress")]
    [InlineData(UnavailableResponsible.InactiveUser, "inProgress")]
    [InlineData(UnavailableResponsible.InactiveMembership, "closed")]
    [InlineData(UnavailableResponsible.InactiveUser, "closed")]
    [InlineData(UnavailableResponsible.OtherTenant, "suspended")]
    public async Task Create_WithUnavailableResponsible_RejectsNeutrallyInAnyStatus(
        UnavailableResponsible unavailable,
        string status)
    {
        TestGraph graph = await SeedGraphAsync();
        Guid requestedMembershipId = unavailable switch
        {
            UnavailableResponsible.OtherTenant => graph.OtherMembership.Id,
            UnavailableResponsible.Missing => Guid.NewGuid(),
            _ => graph.ResponsibleMembership.Id
        };

        if (unavailable is UnavailableResponsible.InactiveMembership or
            UnavailableResponsible.InactiveUser)
        {
            await MakeResponsibleUnavailableAsync(graph, unavailable);
        }

        CreateLegalProcessResult result = await CreateAsync(
            graph,
            "Unavailable Responsible",
            status: status,
            responsibleMembershipId: requestedMembershipId);

        Assert.Same(CreateLegalProcessResult.RelatedResponsibleUnavailable, result);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.False(await dbContext.LegalProcesses.AnyAsync(
            legalProcess => legalProcess.OrganizationId == graph.OrganizationA.Id));
        Assert.False(await dbContext.AuditLogs.AnyAsync());
    }

    private Task<CreateLegalProcessResult> CreateAsync(
        TestGraph graph,
        string title,
        string? processNumber = null,
        string? status = null,
        string? courtOrAuthority = null,
        Guid? responsibleMembershipId = null)
    {
        return CreateAsync(
            graph.OwnerUser.Id,
            graph.OrganizationA.Id,
            graph.ClientA.Id,
            title,
            processNumber,
            status,
            courtOrAuthority,
            responsibleMembershipId);
    }

    private async Task<CreateLegalProcessResult> CreateAsync(
        Guid userId,
        Guid organizationId,
        Guid clientId,
        string title,
        string? processNumber = null,
        string? status = null,
        string? courtOrAuthority = null,
        Guid? responsibleMembershipId = null)
    {
        await using EnmaDbContext operationContext = fixture.CreateDbContext();
        var timeProvider = new FixedTimeProvider(CreatedAt.AddHours(1));
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;
        var useCase = new CreateLegalProcessUseCase(
            new ProcessActionAuthorization(
                new OrganizationAccessAuthorization(
                    new OrganizationAccessLookup(operationContext))),
            new ActiveClientInOrganizationLookup(operationContext),
            new LegalProcessCreationPersistence(options, timeProvider),
            timeProvider);

        return await useCase.ExecuteAsync(new CreateLegalProcessCommand(
            userId,
            organizationId,
            clientId,
            title,
            processNumber,
            status,
            courtOrAuthority,
            responsibleMembershipId));
    }

    private async Task MakeResponsibleUnavailableAsync(
        TestGraph graph,
        UnavailableResponsible unavailable)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        if (unavailable == UnavailableResponsible.InactiveMembership)
        {
            OrganizationMembership membership = await dbContext.OrganizationMemberships
                .SingleAsync(candidate => candidate.Id == graph.ResponsibleMembership.Id);
            membership.Deactivate();
        }
        else
        {
            User user = await dbContext.Users
                .SingleAsync(candidate => candidate.Id == graph.ResponsibleUser.Id);
            user.Deactivate();
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task<LegalProcess> GetProcessAsync(Guid processId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalProcesses
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == processId);
    }

    private async Task<List<AuditLog>> GetAuditLogsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuditLogs
            .AsNoTracking()
            .OrderBy(auditLog => auditLog.OccurredAt)
            .ToListAsync();
    }

    private async Task<List<string?>> GetRawAuditDetailsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuditLogs
            .AsNoTracking()
            .Select(auditLog => EF.Property<string?>(auditLog, "_detailsJson"))
            .ToListAsync();
    }

    private static Guid AssertProcessId(CreateLegalProcessResult result)
    {
        Assert.Equal(CreateLegalProcessResultStatus.Succeeded, result.Status);
        return Assert.IsType<Guid>(result.ProcessId);
    }

    private async Task<TestGraph> SeedGraphAsync()
    {
        var organizationA = new Organization(
            "Creation A",
            $"creation-a-{Guid.NewGuid():N}",
            CreatedAt);
        var organizationB = new Organization(
            "Creation B",
            $"creation-b-{Guid.NewGuid():N}",
            CreatedAt);
        var ownerUser = new User(
            "Creation Owner",
            $"creation-owner-{Guid.NewGuid():N}@example.test",
            CreatedAt);
        var responsibleUser = new User(
            "Creation Responsible",
            $"creation-responsible-{Guid.NewGuid():N}@example.test",
            CreatedAt);
        var otherUser = new User(
            "Creation Other",
            $"creation-other-{Guid.NewGuid():N}@example.test",
            CreatedAt);
        var ownerMembership = new OrganizationMembership(
            organizationA.Id,
            ownerUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var responsibleMembership = new OrganizationMembership(
            organizationA.Id,
            responsibleUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        var otherMembership = new OrganizationMembership(
            organizationB.Id,
            otherUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var clientA = new Client(organizationA.Id, "Creation Client A", CreatedAt);
        var clientB = new Client(organizationB.Id, "Creation Client B", CreatedAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(
            organizationA,
            organizationB,
            ownerUser,
            responsibleUser,
            otherUser,
            ownerMembership,
            responsibleMembership,
            otherMembership,
            clientA,
            clientB);
        await dbContext.SaveChangesAsync();

        return new TestGraph(
            organizationA,
            organizationB,
            ownerUser,
            ownerMembership,
            responsibleUser,
            responsibleMembership,
            otherUser,
            otherMembership,
            clientA,
            clientB);
    }

    public enum UnavailableResponsible
    {
        OtherTenant = 0,
        Missing = 1,
        InactiveMembership = 2,
        InactiveUser = 3
    }

    private sealed record TestGraph(
        Organization OrganizationA,
        Organization OrganizationB,
        User OwnerUser,
        OrganizationMembership OwnerMembership,
        User ResponsibleUser,
        OrganizationMembership ResponsibleMembership,
        User OtherUser,
        OrganizationMembership OtherMembership,
        Client ClientA,
        Client ClientB);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}
