using Enma.Application.Authorization;
using Enma.Application.Tasks;
using Enma.Application.Tasks.Create;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Enma.IntegrationTests.Application.Tasks;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalTaskCreationUseCasePersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset SeededAt = new(
        2026,
        8,
        14,
        20,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateTimeOffset TaskCreatedAt = SeededAt.AddHours(2);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(PrivilegedCreateMatrix))]
    public async Task ExecuteAsync_OwnerOrAdministratorAssignmentMatrix_Persists(
        OrganizationRole role,
        AssignmentSelection assignment)
    {
        TenantMembers graph = CreateTenantMembers(role);
        await SeedAsync(
            graph.Organization,
            graph.ActorUser,
            graph.ActorMembership,
            graph.TargetUser,
            graph.TargetMembership);
        Guid? assigneeMembershipId = assignment switch
        {
            AssignmentSelection.None => null,
            AssignmentSelection.Self => graph.ActorMembership.Id,
            AssignmentSelection.Other => graph.TargetMembership.Id,
            _ => throw new ArgumentOutOfRangeException(nameof(assignment))
        };
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        CreateLegalTaskUseCase useCase = CreateUseCase(queryContext);

        CreateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                processId: null,
                assigneeMembershipId));

        Assert.Equal(CreateLegalTaskResultStatus.Succeeded, result.Status);
        LegalTask persisted = await FindTaskAsync(AssertTaskId(result));
        Assert.Equal(graph.Organization.Id, persisted.OrganizationId);
        Assert.Equal(graph.ActorMembership.Id, persisted.CreatedByMembershipId);
        Assert.Equal(assigneeMembershipId, persisted.AssigneeMembershipId);
        Assert.Equal(TaskCreatedAt, persisted.CreatedAt);

        AuditLog auditLog = await FindSingleAuditLogAsync();
        Assert.Equal(graph.Organization.Id, auditLog.OrganizationId);
        Assert.Equal(graph.ActorUser.Id, auditLog.ActorUserId);
        Assert.Equal(graph.ActorMembership.Id, auditLog.ActorMembershipId);
        Assert.Equal(role, auditLog.ActorRoleAtOccurrence);
        Assert.Equal(AuditEventType.LegalTaskCreated, auditLog.EventType);
        Assert.Equal(AuditEntityType.LegalTask, auditLog.EntityType);
        Assert.Equal(persisted.Id, auditLog.EntityId);
        Assert.Equal(TaskCreatedAt, auditLog.OccurredAt);
        Assert.Null(auditLog.Details);
    }

    [Fact]
    public async Task ExecuteAsync_AuditInsertFailure_RollsBackTask()
    {
        TenantMembers graph = CreateTenantMembers(OrganizationRole.Owner);
        await SeedAsync(
            graph.Organization,
            graph.ActorUser,
            graph.ActorMembership,
            graph.TargetUser,
            graph.TargetMembership);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => CreateUseCase(queryContext, new InvalidNullDetailsInterceptor())
                .ExecuteAsync(CreateCommand(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    processId: null,
                    assigneeMembershipId: null)));

        PostgresException postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal(
            "ck_audit_logs_details_contract",
            postgresException.ConstraintName);
        Assert.Equal(0, await CountTasksAsync());
        Assert.Equal(0, await CountAuditLogsAsync());
    }

    [Fact]
    public async Task ExecuteAsync_MemberAssignmentMatrix_AllowsUnassignedAndSelfOnly()
    {
        TenantMembers graph = CreateTenantMembers(OrganizationRole.Member);
        await SeedAsync(
            graph.Organization,
            graph.ActorUser,
            graph.ActorMembership,
            graph.TargetUser,
            graph.TargetMembership);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        CreateLegalTaskUseCase useCase = CreateUseCase(queryContext);

        CreateLegalTaskResult unassigned = await useCase.ExecuteAsync(
            CreateCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                processId: null,
                assigneeMembershipId: null,
                title: "Unassigned"));
        CreateLegalTaskResult selfAssigned = await useCase.ExecuteAsync(
            CreateCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                processId: null,
                graph.ActorMembership.Id,
                title: "Self assigned"));
        CreateLegalTaskResult otherAssigned = await useCase.ExecuteAsync(
            CreateCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                processId: null,
                graph.TargetMembership.Id,
                title: "Other assigned"));
        CreateLegalTaskResult randomAssigned = await useCase.ExecuteAsync(
            CreateCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                processId: null,
                Guid.NewGuid(),
                title: "Random assigned"));

        Assert.Equal(CreateLegalTaskResultStatus.Succeeded, unassigned.Status);
        Assert.Equal(CreateLegalTaskResultStatus.Succeeded, selfAssigned.Status);
        Assert.Same(CreateLegalTaskResult.AccessDenied, otherAssigned);
        Assert.Same(CreateLegalTaskResult.AccessDenied, randomAssigned);
        Assert.Equal(2, await CountTasksAsync());
    }

    [Fact]
    public async Task ExecuteAsync_InactiveActorUser_ReturnsAccessDeniedWithoutPersistence()
    {
        TenantMembers graph = CreateTenantMembers(OrganizationRole.Owner);
        graph.ActorUser.Deactivate();
        await SeedAsync(
            graph.Organization,
            graph.ActorUser,
            graph.ActorMembership,
            graph.TargetUser,
            graph.TargetMembership);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        CreateLegalTaskUseCase useCase = CreateUseCase(queryContext);

        CreateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                processId: null,
                assigneeMembershipId: null));

        Assert.Same(CreateLegalTaskResult.AccessDenied, result);
        Assert.Equal(0, await CountTasksAsync());
    }

    [Fact]
    public async Task ExecuteAsync_OrganizationDeactivatedAfterAuthorization_DeniesWithoutPersistence()
    {
        TenantMembers graph = CreateTenantMembers(OrganizationRole.Owner);
        await SeedAsync(
            graph.Organization,
            graph.ActorUser,
            graph.ActorMembership,
            graph.TargetUser,
            graph.TargetMembership);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        var options = new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        var persistence = new BeforeCreationPersistence(
            new LegalTaskCreationPersistence(
                options,
                new FixedTimeProvider(TaskCreatedAt)),
            () => DeactivateOrganizationAsync(graph.Organization.Id));
        var useCase = new CreateLegalTaskUseCase(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(queryContext)),
            new ProcessOrganizationOwnershipLookup(queryContext),
            persistence,
            new FixedTimeProvider(TaskCreatedAt));

        CreateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                processId: null,
                assigneeMembershipId: null));

        Assert.Same(CreateLegalTaskResult.AccessDenied, result);
        Assert.Equal(0, await CountTasksAsync());
        Assert.Equal(0, await CountAuditLogsAsync());
    }

    [Theory]
    [InlineData(AssigneeAvailability.Missing)]
    [InlineData(AssigneeAvailability.CrossTenant)]
    [InlineData(AssigneeAvailability.InactiveMembership)]
    [InlineData(AssigneeAvailability.InactiveUser)]
    public async Task ExecuteAsync_UnavailableAssignee_CollapsesResultAndDoesNotPersist(
        AssigneeAvailability availability)
    {
        TenantMembers graph = CreateTenantMembers(OrganizationRole.Owner);
        var otherOrganization = new Organization(
            "Other Organization",
            "other-organization",
            SeededAt);
        OrganizationMembership requestedMembership = graph.TargetMembership;

        if (availability == AssigneeAvailability.CrossTenant)
        {
            requestedMembership = new OrganizationMembership(
                otherOrganization.Id,
                graph.TargetUser.Id,
                OrganizationRole.Member,
                SeededAt);
        }
        else if (availability == AssigneeAvailability.InactiveMembership)
        {
            requestedMembership.Deactivate();
        }
        else if (availability == AssigneeAvailability.InactiveUser)
        {
            graph.TargetUser.Deactivate();
        }

        var entities = new List<object>
        {
            graph.Organization,
            graph.ActorUser,
            graph.ActorMembership,
            graph.TargetUser
        };

        if (availability == AssigneeAvailability.CrossTenant)
        {
            entities.Add(otherOrganization);
        }

        if (availability != AssigneeAvailability.Missing)
        {
            entities.Add(requestedMembership);
        }

        await SeedAsync(entities.ToArray());
        Guid requestedId = availability == AssigneeAvailability.Missing
            ? Guid.NewGuid()
            : requestedMembership.Id;
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        CreateLegalTaskUseCase useCase = CreateUseCase(queryContext);

        CreateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                processId: null,
                requestedId));

        Assert.Same(CreateLegalTaskResult.RelatedAssigneeUnavailable, result);
        Assert.Equal(0, await CountTasksAsync());
    }

    [Fact]
    public async Task ExecuteAsync_ProcessMatrix_UsesTenantOwnershipAndIgnoresClientActivity()
    {
        TenantMembers graph = CreateTenantMembers(OrganizationRole.Owner);
        var otherOrganization = new Organization(
            "Other Organization",
            "other-organization",
            SeededAt);
        var inactiveClient = new Client(
            graph.Organization.Id,
            "Inactive Client",
            SeededAt);
        inactiveClient.Deactivate();
        var sameTenantProcess = new LegalProcess(
            graph.Organization.Id,
            inactiveClient.Id,
            "Same-tenant Process",
            SeededAt);
        var otherClient = new Client(
            otherOrganization.Id,
            "Other Client",
            SeededAt);
        var crossTenantProcess = new LegalProcess(
            otherOrganization.Id,
            otherClient.Id,
            "Cross-tenant Process",
            SeededAt);
        await SeedAsync(
            graph.Organization,
            otherOrganization,
            graph.ActorUser,
            graph.ActorMembership,
            inactiveClient,
            sameTenantProcess,
            otherClient,
            crossTenantProcess);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        CreateLegalTaskUseCase useCase = CreateUseCase(queryContext);

        CreateLegalTaskResult noProcess = await useCase.ExecuteAsync(
            CreateCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                processId: null,
                assigneeMembershipId: null,
                title: "No process"));
        CreateLegalTaskResult sameTenant = await useCase.ExecuteAsync(
            CreateCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                sameTenantProcess.Id,
                assigneeMembershipId: null,
                title: "Same tenant"));
        CreateLegalTaskResult missing = await useCase.ExecuteAsync(
            CreateCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                Guid.NewGuid(),
                assigneeMembershipId: null,
                title: "Missing"));
        CreateLegalTaskResult crossTenant = await useCase.ExecuteAsync(
            CreateCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                crossTenantProcess.Id,
                assigneeMembershipId: null,
                title: "Cross tenant"));

        Assert.Equal(CreateLegalTaskResultStatus.Succeeded, noProcess.Status);
        Assert.Equal(CreateLegalTaskResultStatus.Succeeded, sameTenant.Status);
        Assert.Same(CreateLegalTaskResult.RelatedProcessUnavailable, missing);
        Assert.Same(missing, crossTenant);
        Assert.Equal(2, await CountTasksAsync());
        LegalTask linkedTask = await FindTaskAsync(AssertTaskId(sameTenant));
        Assert.Equal(sameTenantProcess.Id, linkedTask.ProcessId);
    }

    [Fact]
    public async Task ExecuteAsync_DualMemberships_UseContextualMembershipIdsAndRoles()
    {
        var organizationA = new Organization(
            "Organization A",
            "organization-a",
            SeededAt);
        var organizationB = new Organization(
            "Organization B",
            "organization-b",
            SeededAt);
        var actorUser = new User(
            "Dual Actor",
            "dual-actor@example.test",
            SeededAt);
        var targetUser = new User(
            "Dual Target",
            "dual-target@example.test",
            SeededAt);
        var actorMembershipA = new OrganizationMembership(
            organizationA.Id,
            actorUser.Id,
            OrganizationRole.Owner,
            SeededAt);
        var actorMembershipB = new OrganizationMembership(
            organizationB.Id,
            actorUser.Id,
            OrganizationRole.Member,
            SeededAt);
        var targetMembershipA = new OrganizationMembership(
            organizationA.Id,
            targetUser.Id,
            OrganizationRole.Member,
            SeededAt);
        var targetMembershipB = new OrganizationMembership(
            organizationB.Id,
            targetUser.Id,
            OrganizationRole.Member,
            SeededAt);
        await SeedAsync(
            organizationA,
            organizationB,
            actorUser,
            targetUser,
            actorMembershipA,
            actorMembershipB,
            targetMembershipA,
            targetMembershipB);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        CreateLegalTaskUseCase useCase = CreateUseCase(queryContext);

        CreateLegalTaskResult contextualTarget = await useCase.ExecuteAsync(
            CreateCommand(
                actorUser.Id,
                organizationA.Id,
                processId: null,
                targetMembershipA.Id,
                title: "Contextual target"));
        CreateLegalTaskResult otherTenantTarget = await useCase.ExecuteAsync(
            CreateCommand(
                actorUser.Id,
                organizationA.Id,
                processId: null,
                targetMembershipB.Id,
                title: "Other tenant target"));

        Assert.Equal(
            CreateLegalTaskResultStatus.Succeeded,
            contextualTarget.Status);
        Assert.Same(
            CreateLegalTaskResult.RelatedAssigneeUnavailable,
            otherTenantTarget);
        LegalTask persisted = await FindTaskAsync(AssertTaskId(contextualTarget));
        Assert.Equal(actorMembershipA.Id, persisted.CreatedByMembershipId);
        Assert.Equal(targetMembershipA.Id, persisted.AssigneeMembershipId);
        Assert.NotEqual(actorMembershipB.Id, persisted.CreatedByMembershipId);
        Assert.NotEqual(targetMembershipB.Id, persisted.AssigneeMembershipId);
    }

    public static TheoryData<OrganizationRole, AssignmentSelection>
        PrivilegedCreateMatrix =>
        new()
        {
            { OrganizationRole.Owner, AssignmentSelection.None },
            { OrganizationRole.Owner, AssignmentSelection.Self },
            { OrganizationRole.Owner, AssignmentSelection.Other },
            { OrganizationRole.Administrator, AssignmentSelection.None },
            { OrganizationRole.Administrator, AssignmentSelection.Self },
            { OrganizationRole.Administrator, AssignmentSelection.Other }
        };

    private CreateLegalTaskUseCase CreateUseCase(
        EnmaDbContext queryContext,
        IInterceptor? interceptor = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString);

        if (interceptor is not null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        return new CreateLegalTaskUseCase(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(queryContext)),
            new ProcessOrganizationOwnershipLookup(queryContext),
            new LegalTaskCreationPersistence(
                optionsBuilder.Options,
                new FixedTimeProvider(TaskCreatedAt)),
            new FixedTimeProvider(TaskCreatedAt));
    }

    private static CreateLegalTaskCommand CreateCommand(
        Guid userId,
        Guid organizationId,
        Guid? processId,
        Guid? assigneeMembershipId,
        string title = "Prepare defense")
    {
        return new CreateLegalTaskCommand(
            userId,
            organizationId,
            title,
            "Review the records",
            new DateOnly(2026, 9, 1),
            processId,
            assigneeMembershipId);
    }

    private async Task<LegalTask> FindTaskAsync(Guid legalTaskId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalTasks
            .AsNoTracking()
            .SingleAsync(legalTask => legalTask.Id == legalTaskId);
    }

    private async Task<int> CountTasksAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalTasks.CountAsync();
    }

    private async Task<AuditLog> FindSingleAuditLogAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuditLogs.AsNoTracking().SingleAsync();
    }

    private async Task<int> CountAuditLogsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuditLogs.CountAsync();
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private async Task DeactivateOrganizationAsync(Guid organizationId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = await dbContext.Organizations
            .SingleAsync(candidate => candidate.Id == organizationId);
        organization.Deactivate();
        await dbContext.SaveChangesAsync();
    }

    private static Guid AssertTaskId(CreateLegalTaskResult result)
    {
        return Assert.IsType<Guid>(result.LegalTaskId);
    }

    private static TenantMembers CreateTenantMembers(OrganizationRole actorRole)
    {
        var organization = new Organization(
            "Legal Organization",
            "legal-organization",
            SeededAt);
        var actorUser = new User(
            "Actor User",
            "actor@example.test",
            SeededAt);
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actorUser.Id,
            actorRole,
            SeededAt);
        var targetUser = new User(
            "Target User",
            "target@example.test",
            SeededAt);
        var targetMembership = new OrganizationMembership(
            organization.Id,
            targetUser.Id,
            OrganizationRole.Member,
            SeededAt);

        return new TenantMembers(
            organization,
            actorUser,
            actorMembership,
            targetUser,
            targetMembership);
    }

    public enum AssignmentSelection
    {
        None = 0,
        Self = 1,
        Other = 2
    }

    public enum AssigneeAvailability
    {
        Missing = 0,
        CrossTenant = 1,
        InactiveMembership = 2,
        InactiveUser = 3
    }

    private sealed record TenantMembers(
        Organization Organization,
        User ActorUser,
        OrganizationMembership ActorMembership,
        User TargetUser,
        OrganizationMembership TargetMembership);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed class BeforeCreationPersistence(
        ILegalTaskCreationPersistence inner,
        Func<Task> beforeExecute) : ILegalTaskCreationPersistence
    {
        public async Task<LegalTaskCreationPersistenceResult> ExecuteAsync(
            LegalTaskCreationPersistenceRequest request,
            Func<LegalTaskCreationLockedState, LegalTaskCreationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            await beforeExecute();
            return await inner.ExecuteAsync(request, decide, cancellationToken);
        }
    }

    private sealed class InvalidNullDetailsInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            EnmaDbContext dbContext = Assert.IsType<EnmaDbContext>(eventData.Context);
            AuditLog auditLog = Assert.Single(
                dbContext.ChangeTracker.Entries<AuditLog>(),
                entry => entry.State == EntityState.Added).Entity;
            dbContext.Entry(auditLog)
                .Property<string?>("_detailsJson")
                .CurrentValue = "{}";
            return ValueTask.FromResult(result);
        }
    }
}
