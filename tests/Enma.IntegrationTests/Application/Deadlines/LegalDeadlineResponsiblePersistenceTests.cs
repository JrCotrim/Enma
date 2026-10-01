using Enma.Application.Authorization;
using Enma.Application.Deadlines.Complete;
using Enma.Application.Deadlines.Create;
using Enma.Application.Deadlines.Reopen;
using Enma.Application.Deadlines.Responsible;
using Enma.Application.Deadlines.Update;
using Enma.Application.Organizations.Members.Lifecycle;
using Enma.Application.Validation;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Application.Deadlines;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDeadlineResponsiblePersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        9,
        30,
        12,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateOnly DueDate = new(2026, 10, 20);

    private readonly AdvancingTimeProvider timeProvider = new(CreatedAt.AddDays(1));

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Create_WithoutResponsible_PersistsNullAndAuditsOnlyCreation()
    {
        TestGraph graph = await SeedGraphAsync();

        CreateLegalDeadlineResult result = await CreateAsync(graph, null);

        Guid deadlineId = Assert.IsType<Guid>(result.DeadlineId);
        Assert.Equal(CreateLegalDeadlineResultStatus.Created, result.Status);
        Assert.Null((await GetDeadlineAsync(deadlineId)).ResponsibleMembershipId);
        AuditLog auditLog = Assert.Single(await GetDeadlineAuditLogsAsync(deadlineId));
        Assert.Equal(AuditEventType.LegalDeadlineCreated, auditLog.EventType);
        Assert.Null(auditLog.Details);
    }

    [Fact]
    public async Task Create_WithAvailableResponsible_PersistsResponsibleAndAuditsOnlyCreation()
    {
        TestGraph graph = await SeedGraphAsync();

        CreateLegalDeadlineResult result = await CreateAsync(
            graph,
            graph.ResponsibleMembership.Id);

        Guid deadlineId = Assert.IsType<Guid>(result.DeadlineId);
        Assert.Equal(CreateLegalDeadlineResultStatus.Created, result.Status);
        Assert.Equal(
            graph.ResponsibleMembership.Id,
            (await GetDeadlineAsync(deadlineId)).ResponsibleMembershipId);
        AuditLog auditLog = Assert.Single(await GetDeadlineAuditLogsAsync(deadlineId));
        Assert.Equal(AuditEventType.LegalDeadlineCreated, auditLog.EventType);
        Assert.Null(auditLog.Details);
    }

    [Theory]
    [InlineData(UnavailableResponsible.OtherTenant)]
    [InlineData(UnavailableResponsible.Missing)]
    [InlineData(UnavailableResponsible.InactiveMembership)]
    [InlineData(UnavailableResponsible.InactiveUser)]
    public async Task Create_WithUnavailableResponsible_IsRejectedNeutrallyWithoutWrites(
        UnavailableResponsible unavailable)
    {
        TestGraph graph = await SeedGraphAsync();
        Guid requestedMembershipId = await PrepareUnavailableAsync(graph, unavailable);

        CreateLegalDeadlineResult result = await CreateAsync(graph, requestedMembershipId);

        Assert.Same(CreateLegalDeadlineResult.RelatedResponsibleUnavailable, result);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.LegalDeadlines.CountAsync());
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Create_WithEmptyResponsible_ThrowsValidationWithoutWrites()
    {
        TestGraph graph = await SeedGraphAsync();

        await Assert.ThrowsAsync<RequestValidationException>(
            () => CreateAsync(graph, Guid.Empty));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.LegalDeadlines.CountAsync());
    }

    [Fact]
    public async Task ChangeResponsible_SetClearAndNoOp_AuditsOnlyEffectiveChanges()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalDeadline deadline = await SeedDeadlineAsync(graph);

        ChangeLegalDeadlineResponsibleResult[] results =
        [
            await ChangeResponsibleAsync(graph, deadline.Id, graph.ResponsibleMembership.Id),
            await ChangeResponsibleAsync(graph, deadline.Id, graph.ResponsibleMembership.Id),
            await ChangeResponsibleAsync(graph, deadline.Id, null),
            await ChangeResponsibleAsync(graph, deadline.Id, null)
        ];

        Assert.All(
            results,
            result => Assert.Equal(ChangeLegalDeadlineResponsibleResult.Succeeded, result));
        LegalDeadline persisted = await GetDeadlineAsync(deadline.Id);
        Assert.Null(persisted.ResponsibleMembershipId);
        AssertUnchangedDetails(deadline, persisted);
        List<AuditLog> auditLogs = await GetDeadlineAuditLogsAsync(deadline.Id);
        Assert.All(auditLogs, auditLog =>
        {
            Assert.Equal(AuditEventType.LegalDeadlineResponsibleChanged, auditLog.EventType);
            Assert.Equal(AuditEntityType.LegalDeadline, auditLog.EntityType);
            Assert.Equal(graph.OwnerMembership.Id, auditLog.ActorMembershipId);
        });
        Assert.Equal(
            [
                (null, graph.ResponsibleMembership.Id),
                ((Guid?)graph.ResponsibleMembership.Id, (Guid?)null)
            ],
            auditLogs
                .Select(auditLog =>
                    Assert.IsType<LegalDeadlineResponsibleChangedAuditDetails>(
                        auditLog.Details))
                .Select(details => (
                    details.OldResponsibleMembershipId,
                    details.NewResponsibleMembershipId))
                .ToArray());
    }

    [Fact]
    public async Task ChangeResponsible_OnCompletedDeadline_AllowsReplacementAndRemoval()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalDeadline deadline = await SeedDeadlineAsync(
            graph,
            graph.ResponsibleMembership.Id,
            completed: true);
        await MakeResponsibleUnavailableAsync(graph, ResponsibleAvailability.InactiveMembership);

        ChangeLegalDeadlineResponsibleResult replace = await ChangeResponsibleAsync(
            graph,
            deadline.Id,
            graph.OwnerMembership.Id);
        ChangeLegalDeadlineResponsibleResult remove = await ChangeResponsibleAsync(
            graph,
            deadline.Id,
            null);

        Assert.Equal(ChangeLegalDeadlineResponsibleResult.Succeeded, replace);
        Assert.Equal(ChangeLegalDeadlineResponsibleResult.Succeeded, remove);
        LegalDeadline persisted = await GetDeadlineAsync(deadline.Id);
        Assert.Null(persisted.ResponsibleMembershipId);
        Assert.Equal(deadline.CompletedAt, persisted.CompletedAt);
        Assert.Equal(2, (await GetDeadlineAuditLogsAsync(deadline.Id)).Count);
    }

    [Theory]
    [InlineData(UnavailableResponsible.OtherTenant)]
    [InlineData(UnavailableResponsible.Missing)]
    [InlineData(UnavailableResponsible.InactiveMembership)]
    [InlineData(UnavailableResponsible.InactiveUser)]
    public async Task ChangeResponsible_WithUnavailableMember_IsRejectedNeutrally(
        UnavailableResponsible unavailable)
    {
        TestGraph graph = await SeedGraphAsync();
        LegalDeadline deadline = await SeedDeadlineAsync(graph);
        Guid requestedMembershipId = await PrepareUnavailableAsync(graph, unavailable);

        ChangeLegalDeadlineResponsibleResult result = await ChangeResponsibleAsync(
            graph,
            deadline.Id,
            requestedMembershipId);

        Assert.Equal(
            ChangeLegalDeadlineResponsibleResult.RelatedResponsibleUnavailable,
            result);
        Assert.Null((await GetDeadlineAsync(deadline.Id)).ResponsibleMembershipId);
        Assert.Empty(await GetDeadlineAuditLogsAsync(deadline.Id));
    }

    [Fact]
    public async Task ChangeResponsible_WithForeignOrMissingDeadline_ReturnsSameNotFound()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalDeadline foreignDeadline = await SeedDeadlineAsync(
            graph,
            organization: graph.OrganizationB,
            process: graph.ProcessB);

        ChangeLegalDeadlineResponsibleResult foreign = await ChangeResponsibleAsync(
            graph,
            foreignDeadline.Id,
            graph.ResponsibleMembership.Id);
        ChangeLegalDeadlineResponsibleResult missing = await ChangeResponsibleAsync(
            graph,
            Guid.NewGuid(),
            graph.ResponsibleMembership.Id);

        Assert.Equal(ChangeLegalDeadlineResponsibleResult.NotFound, foreign);
        Assert.Equal(foreign, missing);
        Assert.Null((await GetDeadlineAsync(foreignDeadline.Id)).ResponsibleMembershipId);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task ChangeResponsible_WithMemberActor_IsDeniedWithoutWrites()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalDeadline deadline = await SeedDeadlineAsync(graph);

        ChangeLegalDeadlineResponsibleResult result = await ChangeResponsibleAsync(
            graph.ResponsibleUser.Id,
            graph.OrganizationA.Id,
            deadline.Id,
            graph.ResponsibleMembership.Id);

        Assert.Equal(ChangeLegalDeadlineResponsibleResult.AccessDenied, result);
        Assert.Null((await GetDeadlineAsync(deadline.Id)).ResponsibleMembershipId);
        Assert.Empty(await GetDeadlineAuditLogsAsync(deadline.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ResponsibleAvailability.Available)]
    [InlineData(ResponsibleAvailability.InactiveMembership)]
    [InlineData(ResponsibleAvailability.InactiveUser)]
    public async Task Reopen_WithCurrentResponsible_RequiresAvailableResponsible(
        ResponsibleAvailability? availability)
    {
        TestGraph graph = await SeedGraphAsync();
        LegalDeadline deadline = await SeedDeadlineAsync(
            graph,
            availability is null ? null : graph.ResponsibleMembership.Id,
            completed: true);

        if (availability is { } unavailable)
        {
            await MakeResponsibleUnavailableAsync(graph, unavailable);
        }

        ReopenLegalDeadlineResult result = await ReopenAsync(graph, deadline.Id);

        LegalDeadline persisted = await GetDeadlineAsync(deadline.Id);
        List<AuditLog> auditLogs = await GetDeadlineAuditLogsAsync(deadline.Id);

        if (availability is null or ResponsibleAvailability.Available)
        {
            Assert.Same(ReopenLegalDeadlineResult.Succeeded, result);
            Assert.Null(persisted.CompletedAt);
            Assert.Equal(
                AuditEventType.LegalDeadlineReopened,
                Assert.Single(auditLogs).EventType);
        }
        else
        {
            Assert.Same(ReopenLegalDeadlineResult.CurrentResponsibleUnavailable, result);
            Assert.Equal(deadline.CompletedAt, persisted.CompletedAt);
            Assert.Empty(auditLogs);
        }

        Assert.Equal(deadline.ResponsibleMembershipId, persisted.ResponsibleMembershipId);
    }

    [Fact]
    public async Task Complete_WithUnavailableResponsible_DoesNotCheckResponsible()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalDeadline deadline = await SeedDeadlineAsync(
            graph,
            graph.ResponsibleMembership.Id);
        await MakeResponsibleUnavailableAsync(graph, ResponsibleAvailability.InactiveUser);

        CompleteLegalDeadlineResult result = await CompleteAsync(graph, deadline.Id);

        Assert.Same(CompleteLegalDeadlineResult.Succeeded, result);
        LegalDeadline persisted = await GetDeadlineAsync(deadline.Id);
        Assert.NotNull(persisted.CompletedAt);
        Assert.Equal(graph.ResponsibleMembership.Id, persisted.ResponsibleMembershipId);
    }

    [Fact]
    public async Task UpdateDetails_PreservesResponsible()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalDeadline deadline = await SeedDeadlineAsync(
            graph,
            graph.ResponsibleMembership.Id);

        UpdateLegalDeadlineResult result = await UpdateAsync(
            graph,
            deadline.Id,
            "Updated responsible deadline",
            DueDate.AddDays(3));

        Assert.Same(UpdateLegalDeadlineResult.Updated, result);
        LegalDeadline persisted = await GetDeadlineAsync(deadline.Id);
        Assert.Equal("Updated responsible deadline", persisted.Title);
        Assert.Equal(DueDate.AddDays(3), persisted.DueDate);
        Assert.Equal(graph.ResponsibleMembership.Id, persisted.ResponsibleMembershipId);
        Assert.Equal(
            AuditEventType.LegalDeadlineDetailsChanged,
            Assert.Single(await GetDeadlineAuditLogsAsync(deadline.Id)).EventType);
    }

    [Theory]
    [InlineData(DeadlineAssignment.None, OrganizationMemberLifecycleMutationPersistenceResult.Succeeded)]
    [InlineData(
        DeadlineAssignment.Pending,
        OrganizationMemberLifecycleMutationPersistenceResult.ActiveAssignmentsConflict)]
    [InlineData(DeadlineAssignment.Completed, OrganizationMemberLifecycleMutationPersistenceResult.Succeeded)]
    [InlineData(
        DeadlineAssignment.ForeignTenantPending,
        OrganizationMemberLifecycleMutationPersistenceResult.Succeeded)]
    public async Task DeactivateMember_ResponsibleForDeadline_ConflictsOnlyWhilePending(
        DeadlineAssignment assignment,
        OrganizationMemberLifecycleMutationPersistenceResult expected)
    {
        TestGraph graph = await SeedGraphAsync();

        if (assignment is DeadlineAssignment.Pending or DeadlineAssignment.Completed)
        {
            await SeedDeadlineAsync(
                graph,
                graph.ResponsibleMembership.Id,
                completed: assignment == DeadlineAssignment.Completed);
        }
        else if (assignment == DeadlineAssignment.ForeignTenantPending)
        {
            await SeedDeadlineAsync(
                graph,
                graph.OtherMembership.Id,
                organization: graph.OrganizationB,
                process: graph.ProcessB);
        }

        OrganizationMemberLifecycleMutationPersistenceResult result =
            await new OrganizationMemberLifecycleMutationPersistence(
                    CreateOptions(),
                    timeProvider)
                .ExecuteAsync(new OrganizationMemberLifecycleMutationPersistenceRequest(
                    graph.OwnerUser.Id,
                    graph.OrganizationA.Id,
                    graph.OwnerMembership.Id,
                    graph.ResponsibleMembership.Id,
                    OrganizationMemberLifecycleOperation.Deactivate));

        Assert.Equal(expected, result);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(
            expected != OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
            await dbContext.OrganizationMemberships
                .Where(membership => membership.Id == graph.ResponsibleMembership.Id)
                .Select(membership => membership.IsActive)
                .SingleAsync());
    }

    private async Task<CreateLegalDeadlineResult> CreateAsync(
        TestGraph graph,
        Guid? responsibleMembershipId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var useCase = new CreateLegalDeadlineUseCase(
            CreateAuthorization(dbContext),
            new ProcessOrganizationOwnershipLookup(dbContext),
            new LegalDeadlineCreationPersistence(CreateOptions(), timeProvider),
            timeProvider);

        return await useCase.ExecuteAsync(
            graph.OwnerUser.Id,
            graph.OrganizationA.Id,
            graph.ProcessA.Id,
            "Responsible deadline",
            DueDate,
            responsibleMembershipId);
    }

    private Task<ChangeLegalDeadlineResponsibleResult> ChangeResponsibleAsync(
        TestGraph graph,
        Guid deadlineId,
        Guid? responsibleMembershipId)
    {
        return ChangeResponsibleAsync(
            graph.OwnerUser.Id,
            graph.OrganizationA.Id,
            deadlineId,
            responsibleMembershipId);
    }

    private async Task<ChangeLegalDeadlineResponsibleResult> ChangeResponsibleAsync(
        Guid userId,
        Guid organizationId,
        Guid deadlineId,
        Guid? responsibleMembershipId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var useCase = new ChangeLegalDeadlineResponsibleUseCase(
            CreateAuthorization(dbContext),
            CreateMutationPersistence());

        return await useCase.ExecuteAsync(new ChangeLegalDeadlineResponsibleCommand(
            userId,
            organizationId,
            deadlineId,
            responsibleMembershipId));
    }

    private async Task<ReopenLegalDeadlineResult> ReopenAsync(
        TestGraph graph,
        Guid deadlineId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var useCase = new ReopenLegalDeadlineUseCase(
            CreateAuthorization(dbContext),
            CreateMutationPersistence());

        return await useCase.ExecuteAsync(
            graph.OwnerUser.Id,
            graph.OrganizationA.Id,
            deadlineId);
    }

    private async Task<CompleteLegalDeadlineResult> CompleteAsync(
        TestGraph graph,
        Guid deadlineId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var useCase = new CompleteLegalDeadlineUseCase(
            CreateAuthorization(dbContext),
            CreateMutationPersistence(),
            timeProvider);

        return await useCase.ExecuteAsync(
            graph.OwnerUser.Id,
            graph.OrganizationA.Id,
            deadlineId);
    }

    private async Task<UpdateLegalDeadlineResult> UpdateAsync(
        TestGraph graph,
        Guid deadlineId,
        string title,
        DateOnly dueDate)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var useCase = new UpdateLegalDeadlineUseCase(
            CreateAuthorization(dbContext),
            CreateMutationPersistence());

        return await useCase.ExecuteAsync(
            graph.OwnerUser.Id,
            graph.OrganizationA.Id,
            deadlineId,
            title,
            dueDate);
    }

    private static DeadlineActionAuthorization CreateAuthorization(
        EnmaDbContext dbContext)
    {
        return new DeadlineActionAuthorization(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(dbContext)));
    }

    private LegalDeadlineMutationPersistence CreateMutationPersistence()
    {
        return new LegalDeadlineMutationPersistence(CreateOptions(), timeProvider);
    }

    private DbContextOptions<EnmaDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
    }

    private async Task<Guid> PrepareUnavailableAsync(
        TestGraph graph,
        UnavailableResponsible unavailable)
    {
        switch (unavailable)
        {
            case UnavailableResponsible.OtherTenant:
                return graph.OtherMembership.Id;
            case UnavailableResponsible.Missing:
                return Guid.NewGuid();
            case UnavailableResponsible.InactiveMembership:
                await MakeResponsibleUnavailableAsync(
                    graph,
                    ResponsibleAvailability.InactiveMembership);
                return graph.ResponsibleMembership.Id;
            case UnavailableResponsible.InactiveUser:
                await MakeResponsibleUnavailableAsync(
                    graph,
                    ResponsibleAvailability.InactiveUser);
                return graph.ResponsibleMembership.Id;
            default:
                throw new ArgumentOutOfRangeException(nameof(unavailable));
        }
    }

    private async Task MakeResponsibleUnavailableAsync(
        TestGraph graph,
        ResponsibleAvailability availability)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        if (availability == ResponsibleAvailability.InactiveMembership)
        {
            OrganizationMembership membership = await dbContext.OrganizationMemberships
                .SingleAsync(candidate => candidate.Id == graph.ResponsibleMembership.Id);
            membership.Deactivate();
        }
        else if (availability == ResponsibleAvailability.InactiveUser)
        {
            User user = await dbContext.Users
                .SingleAsync(candidate => candidate.Id == graph.ResponsibleUser.Id);
            user.Deactivate();
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task<LegalDeadline> SeedDeadlineAsync(
        TestGraph graph,
        Guid? responsibleMembershipId = null,
        bool completed = false,
        Organization? organization = null,
        LegalProcess? process = null)
    {
        var legalDeadline = new LegalDeadline(
            (organization ?? graph.OrganizationA).Id,
            (process ?? graph.ProcessA).Id,
            "Seeded responsible deadline",
            DueDate,
            CreatedAt,
            responsibleMembershipId);

        if (completed)
        {
            legalDeadline.Complete(CreatedAt.AddHours(1));
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.LegalDeadlines.Add(legalDeadline);
        await dbContext.SaveChangesAsync();
        return legalDeadline;
    }

    private async Task<LegalDeadline> GetDeadlineAsync(Guid deadlineId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalDeadlines
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == deadlineId);
    }

    private async Task<List<AuditLog>> GetDeadlineAuditLogsAsync(Guid deadlineId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuditLogs
            .AsNoTracking()
            .Where(auditLog => auditLog.EntityId == deadlineId)
            .OrderBy(auditLog => auditLog.OccurredAt)
            .ToListAsync();
    }

    private static void AssertUnchangedDetails(
        LegalDeadline original,
        LegalDeadline persisted)
    {
        Assert.Equal(original.Id, persisted.Id);
        Assert.Equal(original.OrganizationId, persisted.OrganizationId);
        Assert.Equal(original.ProcessId, persisted.ProcessId);
        Assert.Equal(original.Title, persisted.Title);
        Assert.Equal(original.DueDate, persisted.DueDate);
        Assert.Equal(original.CreatedAt, persisted.CreatedAt);
        Assert.Equal(original.CompletedAt, persisted.CompletedAt);
    }

    private async Task<TestGraph> SeedGraphAsync()
    {
        var organizationA = new Organization(
            "Deadline Responsible A",
            $"deadline-responsible-a-{Guid.NewGuid():N}",
            CreatedAt);
        var organizationB = new Organization(
            "Deadline Responsible B",
            $"deadline-responsible-b-{Guid.NewGuid():N}",
            CreatedAt);
        var ownerUser = new User(
            "Deadline Responsible Owner",
            $"deadline-responsible-owner-{Guid.NewGuid():N}@example.test",
            CreatedAt);
        var responsibleUser = new User(
            "Deadline Responsible Member",
            $"deadline-responsible-member-{Guid.NewGuid():N}@example.test",
            CreatedAt);
        var otherUser = new User(
            "Deadline Responsible Other",
            $"deadline-responsible-other-{Guid.NewGuid():N}@example.test",
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
            OrganizationRole.Member,
            CreatedAt);
        var clientA = new Client(organizationA.Id, "Deadline Client A", CreatedAt);
        var clientB = new Client(organizationB.Id, "Deadline Client B", CreatedAt);
        var processA = new LegalProcess(
            organizationA.Id,
            clientA.Id,
            "Deadline Process A",
            CreatedAt);
        var processB = new LegalProcess(
            organizationB.Id,
            clientB.Id,
            "Deadline Process B",
            CreatedAt);

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
            clientB,
            processA,
            processB);
        await dbContext.SaveChangesAsync();

        return new TestGraph(
            organizationA,
            organizationB,
            ownerUser,
            ownerMembership,
            responsibleUser,
            responsibleMembership,
            otherMembership,
            processA,
            processB);
    }

    public enum ResponsibleAvailability
    {
        Available = 0,
        InactiveMembership = 1,
        InactiveUser = 2
    }

    public enum UnavailableResponsible
    {
        OtherTenant = 0,
        Missing = 1,
        InactiveMembership = 2,
        InactiveUser = 3
    }

    public enum DeadlineAssignment
    {
        None = 0,
        Pending = 1,
        Completed = 2,
        ForeignTenantPending = 3
    }

    private sealed record TestGraph(
        Organization OrganizationA,
        Organization OrganizationB,
        User OwnerUser,
        OrganizationMembership OwnerMembership,
        User ResponsibleUser,
        OrganizationMembership ResponsibleMembership,
        OrganizationMembership OtherMembership,
        LegalProcess ProcessA,
        LegalProcess ProcessB);

    private sealed class AdvancingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private long _ticks;

        public override DateTimeOffset GetUtcNow()
        {
            return start.AddSeconds(Interlocked.Increment(ref _ticks));
        }
    }
}
