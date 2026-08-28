using Enma.Application.Authorization;
using Enma.Application.Tasks;
using Enma.Application.Tasks.Assignment;
using Enma.Application.Tasks.Complete;
using Enma.Application.Tasks.Reopen;
using Enma.Application.Tasks.Update;
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
public sealed class LegalTaskMutationUseCasesPersistenceTests(
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
    private static readonly DateTimeOffset TaskCreatedAt = SeededAt.AddHours(1);
    private static readonly DateTimeOffset TaskCompletedAt = SeededAt.AddHours(2);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task UpdateAsync_PrivilegedRole_UpdatesAnyPendingTask(
        OrganizationRole role)
    {
        TenantGraph graph = await SeedTenantAsync(role);
        LegalTask legalTask = CreateTask(
            graph,
            graph.TargetMembership.Id,
            graph.TargetMembership.Id);
        await SeedAsync(legalTask);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        UpdateLegalTaskUseCase useCase = CreateUpdateUseCase(queryContext);

        UpdateLegalTaskResult result = await useCase.ExecuteAsync(
            new UpdateLegalTaskCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                legalTask.Id,
                "  Updated privileged task  ",
                "  New details  ",
                new DateOnly(2026, 9, 10),
                null));

        Assert.Equal(UpdateLegalTaskResult.Succeeded, result);
        LegalTask persisted = await FindTaskAsync(legalTask.Id);
        Assert.Equal("Updated privileged task", persisted.Title);
        Assert.Equal("New details", persisted.Description);

        AuditLog auditLog = Assert.Single(await FindAuditLogsAsync());
        Assert.Equal(graph.ActorUser.Id, auditLog.ActorUserId);
        Assert.Equal(graph.ActorMembership.Id, auditLog.ActorMembershipId);
        Assert.Equal(role, auditLog.ActorRoleAtOccurrence);
        Assert.Equal(AuditEventType.LegalTaskDetailsChanged, auditLog.EventType);
        Assert.Equal(legalTask.Id, auditLog.EntityId);
        LegalTaskDetailsChangedAuditDetails details =
            Assert.IsType<LegalTaskDetailsChangedAuditDetails>(auditLog.Details);
        Assert.Equal(
            [
                LegalTaskChangedField.Title,
                LegalTaskChangedField.Description,
                LegalTaskChangedField.DueDate
            ],
            details.ChangedFields);
    }

    [Fact]
    public async Task UpdateAsync_NormalizedNoOp_CreatesNoAudit()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        LegalTask legalTask = CreateTask(
            graph,
            null,
            graph.ActorMembership.Id);
        await SeedAsync(legalTask);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();

        UpdateLegalTaskResult result = await CreateUpdateUseCase(queryContext)
            .ExecuteAsync(new UpdateLegalTaskCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                legalTask.Id,
                "  Original task  ",
                "   ",
                null,
                null));

        Assert.Equal(UpdateLegalTaskResult.Succeeded, result);
        Assert.Empty(await FindAuditLogsAsync());
    }

    [Fact]
    public async Task UpdateAsync_MemberOwnershipMatrix_UsesAssigneeThenUnassignedCreator()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Member);
        LegalTask selfAssigned = CreateTask(
            graph,
            graph.ActorMembership.Id,
            graph.TargetMembership.Id,
            "Self assigned");
        LegalTask unassignedCreated = CreateTask(
            graph,
            null,
            graph.ActorMembership.Id,
            "Unassigned created");
        LegalTask unassignedOther = CreateTask(
            graph,
            null,
            graph.TargetMembership.Id,
            "Unassigned other");
        LegalTask assignedOther = CreateTask(
            graph,
            graph.TargetMembership.Id,
            graph.ActorMembership.Id,
            "Assigned other");
        await SeedAsync(
            selfAssigned,
            unassignedCreated,
            unassignedOther,
            assignedOther);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        UpdateLegalTaskUseCase useCase = CreateUpdateUseCase(queryContext);

        UpdateLegalTaskResult selfResult = await UpdateTitleAsync(
            useCase,
            graph,
            selfAssigned.Id,
            "Self updated");
        UpdateLegalTaskResult creatorResult = await UpdateTitleAsync(
            useCase,
            graph,
            unassignedCreated.Id,
            "Creator updated");
        UpdateLegalTaskResult unassignedOtherResult = await UpdateTitleAsync(
            useCase,
            graph,
            unassignedOther.Id,
            "Must not update");
        UpdateLegalTaskResult assignedOtherResult = await UpdateTitleAsync(
            useCase,
            graph,
            assignedOther.Id,
            "Must not update");

        Assert.Equal(UpdateLegalTaskResult.Succeeded, selfResult);
        Assert.Equal(UpdateLegalTaskResult.Succeeded, creatorResult);
        Assert.Equal(UpdateLegalTaskResult.AccessDenied, unassignedOtherResult);
        Assert.Equal(UpdateLegalTaskResult.AccessDenied, assignedOtherResult);
        Assert.Equal("Unassigned other", (await FindTaskAsync(unassignedOther.Id)).Title);
        Assert.Equal("Assigned other", (await FindTaskAsync(assignedOther.Id)).Title);
    }

    [Fact]
    public async Task UpdateAsync_ProcessSetChangeClear_UsesTenantProcessOnlyAndIgnoresClientActivity()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        var inactiveClient = new Client(
            graph.Organization.Id,
            "Inactive client",
            SeededAt);
        inactiveClient.Deactivate();
        var firstProcess = new LegalProcess(
            graph.Organization.Id,
            inactiveClient.Id,
            "First process",
            SeededAt);
        var secondProcess = new LegalProcess(
            graph.Organization.Id,
            inactiveClient.Id,
            "Second process",
            SeededAt);
        var otherOrganization = new Organization(
            "Other organization",
            "other-process-organization",
            SeededAt);
        var otherClient = new Client(
            otherOrganization.Id,
            "Other client",
            SeededAt);
        var otherProcess = new LegalProcess(
            otherOrganization.Id,
            otherClient.Id,
            "Other process",
            SeededAt);
        LegalTask legalTask = CreateTask(
            graph,
            null,
            graph.ActorMembership.Id);
        await SeedAsync(
            inactiveClient,
            firstProcess,
            secondProcess,
            otherOrganization,
            otherClient,
            otherProcess,
            legalTask);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        UpdateLegalTaskUseCase useCase = CreateUpdateUseCase(queryContext);

        UpdateLegalTaskResult set = await UpdateProcessAsync(
            useCase,
            graph,
            legalTask.Id,
            firstProcess.Id,
            "Set process");
        UpdateLegalTaskResult change = await UpdateProcessAsync(
            useCase,
            graph,
            legalTask.Id,
            secondProcess.Id,
            "Change process");
        UpdateLegalTaskResult crossTenant = await UpdateProcessAsync(
            useCase,
            graph,
            legalTask.Id,
            otherProcess.Id,
            "Cross tenant");
        UpdateLegalTaskResult missing = await UpdateProcessAsync(
            useCase,
            graph,
            legalTask.Id,
            Guid.NewGuid(),
            "Missing");
        UpdateLegalTaskResult clear = await UpdateProcessAsync(
            useCase,
            graph,
            legalTask.Id,
            null,
            "Clear process");

        Assert.Equal(UpdateLegalTaskResult.Succeeded, set);
        Assert.Equal(UpdateLegalTaskResult.Succeeded, change);
        Assert.Equal(UpdateLegalTaskResult.RelatedProcessUnavailable, crossTenant);
        Assert.Equal(UpdateLegalTaskResult.RelatedProcessUnavailable, missing);
        Assert.Equal(UpdateLegalTaskResult.Succeeded, clear);
        LegalTask persisted = await FindTaskAsync(legalTask.Id);
        Assert.Equal("Clear process", persisted.Title);
        Assert.Null(persisted.ProcessId);

        AuditLog[] auditLogs = await FindAuditLogsAsync();
        Assert.Equal(3, auditLogs.Length);
        Assert.All(auditLogs, auditLog =>
        {
            LegalTaskDetailsChangedAuditDetails details =
                Assert.IsType<LegalTaskDetailsChangedAuditDetails>(
                    auditLog.Details);
            Assert.Equal(
                [
                    LegalTaskChangedField.Title,
                    LegalTaskChangedField.ProcessId
                ],
                details.ChangedFields);
        });
    }

    [Fact]
    public async Task UpdateAsync_CompletedConflictAndInvalidInput_RollBackAllDetails()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        LegalTask completed = CreateTask(
            graph,
            null,
            graph.ActorMembership.Id,
            "Completed",
            completed: true);
        LegalTask pending = CreateTask(
            graph,
            null,
            graph.ActorMembership.Id,
            "Pending");
        await SeedAsync(completed, pending);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        UpdateLegalTaskUseCase useCase = CreateUpdateUseCase(queryContext);

        UpdateLegalTaskResult conflict = await UpdateTitleAsync(
            useCase,
            graph,
            completed.Id,
            "Forbidden update");
        UpdateLegalTaskResult invalid = await UpdateTitleAsync(
            useCase,
            graph,
            pending.Id,
            "   ");

        Assert.Equal(UpdateLegalTaskResult.Conflict, conflict);
        Assert.Equal(UpdateLegalTaskResult.InvalidInput, invalid);
        Assert.Equal("Completed", (await FindTaskAsync(completed.Id)).Title);
        Assert.Equal("Pending", (await FindTaskAsync(pending.Id)).Title);
    }

    [Fact]
    public async Task ChangeAssigneeAsync_PrivilegedRole_AssignsReassignsAndUnassigns()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Administrator);
        LegalTask legalTask = CreateTask(
            graph,
            null,
            graph.ActorMembership.Id);
        await SeedAsync(legalTask);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        ChangeLegalTaskAssigneeUseCase useCase = CreateAssignmentUseCase(queryContext);

        ChangeLegalTaskAssigneeResult assign = await ChangeAssigneeAsync(
            useCase,
            graph,
            legalTask.Id,
            graph.TargetMembership.Id);
        ChangeLegalTaskAssigneeResult reassign = await ChangeAssigneeAsync(
            useCase,
            graph,
            legalTask.Id,
            graph.ActorMembership.Id);
        ChangeLegalTaskAssigneeResult unassign = await ChangeAssigneeAsync(
            useCase,
            graph,
            legalTask.Id,
            null);

        Assert.Equal(ChangeLegalTaskAssigneeResult.Succeeded, assign);
        Assert.Equal(ChangeLegalTaskAssigneeResult.Succeeded, reassign);
        Assert.Equal(ChangeLegalTaskAssigneeResult.Succeeded, unassign);
        Assert.Null((await FindTaskAsync(legalTask.Id)).AssigneeMembershipId);

        AuditLog[] auditLogs = await FindAuditLogsAsync();
        Assert.Equal(3, auditLogs.Length);
        Assert.All(auditLogs, auditLog =>
        {
            Assert.Equal(graph.ActorMembership.Id, auditLog.ActorMembershipId);
            Assert.Equal(AuditEventType.LegalTaskAssigneeChanged, auditLog.EventType);
            Assert.Equal(legalTask.Id, auditLog.EntityId);
        });
        LegalTaskAssigneeChangedAuditDetails[] details = auditLogs
            .Select(auditLog => Assert.IsType<LegalTaskAssigneeChangedAuditDetails>(
                auditLog.Details))
            .ToArray();
        Assert.Contains(details, item =>
            item.OldAssigneeMembershipId is null &&
            item.NewAssigneeMembershipId == graph.TargetMembership.Id);
        Assert.Contains(details, item =>
            item.OldAssigneeMembershipId == graph.TargetMembership.Id &&
            item.NewAssigneeMembershipId == graph.ActorMembership.Id);
        Assert.Contains(details, item =>
            item.OldAssigneeMembershipId == graph.ActorMembership.Id &&
            item.NewAssigneeMembershipId is null);
    }

    [Fact]
    public async Task ChangeAssigneeAsync_Member_UsesExactTransitionMatrix()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Member);
        LegalTask unassignedNoOp = CreateTask(
            graph,
            null,
            graph.ActorMembership.Id,
            "Null to null");
        LegalTask unassignedClaim = CreateTask(
            graph,
            null,
            graph.TargetMembership.Id,
            "Null to self");
        LegalTask selfNoOp = CreateTask(
            graph,
            graph.ActorMembership.Id,
            graph.TargetMembership.Id,
            "Self to self");
        LegalTask selfRelease = CreateTask(
            graph,
            graph.ActorMembership.Id,
            graph.TargetMembership.Id,
            "Self to null");
        LegalTask unassignedOther = CreateTask(
            graph,
            null,
            graph.ActorMembership.Id,
            "Null to other");
        LegalTask selfOther = CreateTask(
            graph,
            graph.ActorMembership.Id,
            graph.TargetMembership.Id,
            "Self to other");
        LegalTask assignedOther = CreateTask(
            graph,
            graph.TargetMembership.Id,
            graph.ActorMembership.Id,
            "Other current");
        await SeedAsync(
            unassignedNoOp,
            unassignedClaim,
            selfNoOp,
            selfRelease,
            unassignedOther,
            selfOther,
            assignedOther);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        ChangeLegalTaskAssigneeUseCase useCase = CreateAssignmentUseCase(queryContext);

        ChangeLegalTaskAssigneeResult nullNoOp = await ChangeAssigneeAsync(
            useCase, graph, unassignedNoOp.Id, null);
        ChangeLegalTaskAssigneeResult claim = await ChangeAssigneeAsync(
            useCase, graph, unassignedClaim.Id, graph.ActorMembership.Id);
        ChangeLegalTaskAssigneeResult selfNoOpResult = await ChangeAssigneeAsync(
            useCase, graph, selfNoOp.Id, graph.ActorMembership.Id);
        ChangeLegalTaskAssigneeResult release = await ChangeAssigneeAsync(
            useCase, graph, selfRelease.Id, null);
        ChangeLegalTaskAssigneeResult nullOther = await ChangeAssigneeAsync(
            useCase, graph, unassignedOther.Id, graph.TargetMembership.Id);
        ChangeLegalTaskAssigneeResult selfToOther = await ChangeAssigneeAsync(
            useCase, graph, selfOther.Id, graph.TargetMembership.Id);
        ChangeLegalTaskAssigneeResult otherToSelf = await ChangeAssigneeAsync(
            useCase, graph, assignedOther.Id, graph.ActorMembership.Id);
        ChangeLegalTaskAssigneeResult otherToNull = await ChangeAssigneeAsync(
            useCase, graph, assignedOther.Id, null);

        Assert.All(
            new[] { nullNoOp, claim, selfNoOpResult, release },
            result => Assert.Equal(ChangeLegalTaskAssigneeResult.Succeeded, result));
        Assert.All(
            new[] { nullOther, selfToOther, otherToSelf, otherToNull },
            result => Assert.Equal(ChangeLegalTaskAssigneeResult.AccessDenied, result));
        Assert.Equal(
            graph.ActorMembership.Id,
            (await FindTaskAsync(unassignedClaim.Id)).AssigneeMembershipId);
        Assert.Null((await FindTaskAsync(selfRelease.Id)).AssigneeMembershipId);
        Assert.Equal(
            graph.TargetMembership.Id,
            (await FindTaskAsync(assignedOther.Id)).AssigneeMembershipId);
        Assert.Equal(2, (await FindAuditLogsAsync()).Length);
    }

    [Theory]
    [InlineData(AssigneeAvailability.Missing)]
    [InlineData(AssigneeAvailability.CrossTenant)]
    [InlineData(AssigneeAvailability.InactiveMembership)]
    [InlineData(AssigneeAvailability.InactiveUser)]
    public async Task ChangeAssigneeAsync_UnavailableTarget_CollapsesResult(
        AssigneeAvailability availability)
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        LegalTask legalTask = CreateTask(
            graph,
            null,
            graph.ActorMembership.Id);
        Guid requestedId;

        if (availability == AssigneeAvailability.Missing)
        {
            requestedId = Guid.NewGuid();
            await SeedAsync(legalTask);
        }
        else if (availability == AssigneeAvailability.CrossTenant)
        {
            var otherOrganization = new Organization(
                "Other assignee organization",
                "other-assignee-organization",
                SeededAt);
            var crossMembership = new OrganizationMembership(
                otherOrganization.Id,
                graph.TargetUser.Id,
                OrganizationRole.Member,
                SeededAt);
            requestedId = crossMembership.Id;
            await SeedAsync(otherOrganization, crossMembership, legalTask);
        }
        else
        {
            if (availability == AssigneeAvailability.InactiveMembership)
            {
                graph.TargetMembership.Deactivate();
            }
            else
            {
                await DeactivateUserAsync(graph.TargetUser.Id);
            }

            requestedId = graph.TargetMembership.Id;
            await UpdateMembershipAsync(graph.TargetMembership);
            await SeedAsync(legalTask);
        }

        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        ChangeLegalTaskAssigneeUseCase useCase = CreateAssignmentUseCase(queryContext);

        ChangeLegalTaskAssigneeResult result = await ChangeAssigneeAsync(
            useCase,
            graph,
            legalTask.Id,
            requestedId);

        Assert.Equal(
            ChangeLegalTaskAssigneeResult.RelatedAssigneeUnavailable,
            result);
        Assert.Null((await FindTaskAsync(legalTask.Id)).AssigneeMembershipId);
    }

    [Fact]
    public async Task ChangeAssigneeAsync_CompletedConflict_PreservesAssignment()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        LegalTask legalTask = CreateTask(
            graph,
            graph.ActorMembership.Id,
            graph.ActorMembership.Id,
            completed: true);
        await SeedAsync(legalTask);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        ChangeLegalTaskAssigneeUseCase useCase = CreateAssignmentUseCase(queryContext);

        ChangeLegalTaskAssigneeResult result = await ChangeAssigneeAsync(
            useCase,
            graph,
            legalTask.Id,
            graph.TargetMembership.Id);

        Assert.Equal(ChangeLegalTaskAssigneeResult.Conflict, result);
        Assert.Equal(
            graph.ActorMembership.Id,
            (await FindTaskAsync(legalTask.Id)).AssigneeMembershipId);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task LifecycleAsync_AuthorizedRole_CompletesAndReopensIdempotently(
        OrganizationRole role)
    {
        TenantGraph graph = await SeedTenantAsync(role);
        Guid? assignee = role == OrganizationRole.Member
            ? graph.ActorMembership.Id
            : graph.TargetMembership.Id;
        LegalTask legalTask = CreateTask(
            graph,
            assignee,
            graph.TargetMembership.Id);
        await SeedAsync(legalTask);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        CompleteLegalTaskUseCase completeUseCase = CreateCompleteUseCase(queryContext);
        ReopenLegalTaskUseCase reopenUseCase = CreateReopenUseCase(queryContext);
        var completeCommand = new CompleteLegalTaskCommand(
            graph.ActorUser.Id,
            graph.Organization.Id,
            legalTask.Id);
        var reopenCommand = new ReopenLegalTaskCommand(
            graph.ActorUser.Id,
            graph.Organization.Id,
            legalTask.Id);

        CompleteLegalTaskResult firstComplete =
            await completeUseCase.ExecuteAsync(completeCommand);
        CompleteLegalTaskResult secondComplete =
            await completeUseCase.ExecuteAsync(completeCommand);
        DateTimeOffset? firstTimestamp =
            (await FindTaskAsync(legalTask.Id)).CompletedAt;
        ReopenLegalTaskResult firstReopen =
            await reopenUseCase.ExecuteAsync(reopenCommand);
        ReopenLegalTaskResult secondReopen =
            await reopenUseCase.ExecuteAsync(reopenCommand);

        Assert.Equal(CompleteLegalTaskResult.Succeeded, firstComplete);
        Assert.Equal(CompleteLegalTaskResult.Succeeded, secondComplete);
        Assert.Equal(TaskCompletedAt, firstTimestamp);
        Assert.Equal(ReopenLegalTaskResult.Succeeded, firstReopen);
        Assert.Equal(ReopenLegalTaskResult.Succeeded, secondReopen);
        Assert.Null((await FindTaskAsync(legalTask.Id)).CompletedAt);

        AuditLog[] auditLogs = await FindAuditLogsAsync();
        Assert.Equal(
            [
                AuditEventType.LegalTaskCompleted,
                AuditEventType.LegalTaskReopened
            ],
            auditLogs
                .OrderBy(auditLog => auditLog.EventType)
                .Select(auditLog => auditLog.EventType)
                .ToArray());
        Assert.All(auditLogs, auditLog =>
        {
            Assert.Equal(graph.ActorMembership.Id, auditLog.ActorMembershipId);
            Assert.Equal(legalTask.Id, auditLog.EntityId);
            Assert.Null(auditLog.Details);
        });
    }

    [Fact]
    public async Task LifecycleAsync_MemberNonOwnTask_DeniesWithoutMutation()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Member);
        LegalTask pending = CreateTask(
            graph,
            graph.TargetMembership.Id,
            graph.ActorMembership.Id,
            "Pending non-own");
        LegalTask completed = CreateTask(
            graph,
            graph.TargetMembership.Id,
            graph.ActorMembership.Id,
            "Completed non-own",
            completed: true);
        await SeedAsync(pending, completed);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();

        CompleteLegalTaskResult complete = await CreateCompleteUseCase(queryContext)
            .ExecuteAsync(new CompleteLegalTaskCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                pending.Id));
        ReopenLegalTaskResult reopen = await CreateReopenUseCase(queryContext)
            .ExecuteAsync(new ReopenLegalTaskCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                completed.Id));

        Assert.Equal(CompleteLegalTaskResult.AccessDenied, complete);
        Assert.Equal(ReopenLegalTaskResult.AccessDenied, reopen);
        Assert.Null((await FindTaskAsync(pending.Id)).CompletedAt);
        Assert.Equal(TaskCompletedAt, (await FindTaskAsync(completed.Id)).CompletedAt);
        Assert.Empty(await FindAuditLogsAsync());
    }

    [Fact]
    public async Task Mutations_WrongTenant_ReturnNotFoundWithoutCrossTenantMutation()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        var otherOrganization = new Organization(
            "Wrong tenant",
            "wrong-tenant-mutations",
            SeededAt);
        LegalTask otherTask = new(
            otherOrganization.Id,
            "Other task",
            null,
            null,
            null,
            null,
            graph.ActorMembership.Id,
            TaskCreatedAt);
        await SeedAsync(otherOrganization);
        var otherUser = new User("Other actor", "other-actor@example.test", SeededAt);
        var otherMembership = new OrganizationMembership(
            otherOrganization.Id,
            otherUser.Id,
            OrganizationRole.Owner,
            SeededAt);
        otherTask = new LegalTask(
            otherOrganization.Id,
            "Other task",
            null,
            null,
            null,
            null,
            otherMembership.Id,
            TaskCreatedAt);
        await SeedAsync(otherUser, otherMembership, otherTask);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();

        UpdateLegalTaskResult update = await UpdateTitleAsync(
            CreateUpdateUseCase(queryContext),
            graph,
            otherTask.Id,
            "Cross update");
        ChangeLegalTaskAssigneeResult assignment = await ChangeAssigneeAsync(
            CreateAssignmentUseCase(queryContext),
            graph,
            otherTask.Id,
            null);
        CompleteLegalTaskResult complete = await CreateCompleteUseCase(queryContext)
            .ExecuteAsync(new CompleteLegalTaskCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                otherTask.Id));
        ReopenLegalTaskResult reopen = await CreateReopenUseCase(queryContext)
            .ExecuteAsync(new ReopenLegalTaskCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                otherTask.Id));

        Assert.Equal(UpdateLegalTaskResult.NotFound, update);
        Assert.Equal(ChangeLegalTaskAssigneeResult.NotFound, assignment);
        Assert.Equal(CompleteLegalTaskResult.NotFound, complete);
        Assert.Equal(ReopenLegalTaskResult.NotFound, reopen);
        LegalTask persisted = await FindTaskAsync(otherTask.Id);
        Assert.Equal("Other task", persisted.Title);
        Assert.Null(persisted.CompletedAt);
        Assert.Empty(await FindAuditLogsAsync());
    }

    [Fact]
    public async Task MutationPersistence_SaveFailure_RollsBackTrackedTaskMutation()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        LegalTask legalTask = CreateTask(
            graph,
            null,
            graph.ActorMembership.Id,
            "Rollback original");
        await SeedAsync(legalTask);
        var request = new LegalTaskMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            legalTask.Id);

        await Assert.ThrowsAsync<DbUpdateException>(() => CreatePersistence().ExecuteAsync(
            request,
            static _ => null,
            state =>
            {
                state.LegalTask.ChangeDetails(
                    "Must roll back",
                    null,
                    null,
                    Guid.NewGuid());
                return LegalTaskMutationDecision.Persist;
            }));

        LegalTask persisted = await FindTaskAsync(legalTask.Id);
        Assert.Equal("Rollback original", persisted.Title);
        Assert.Null(persisted.ProcessId);
        Assert.Empty(await FindAuditLogsAsync());
    }

    [Fact]
    public async Task MutationPersistence_AuditInsertFailure_RollsBackTask()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        LegalTask legalTask = CreateTask(
            graph,
            null,
            graph.ActorMembership.Id,
            "Audit rollback original");
        await SeedAsync(legalTask);
        var request = new LegalTaskMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            legalTask.Id);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => CreatePersistence(new InvalidAuditDetailsInterceptor(null)).ExecuteAsync(
                request,
                static _ => null,
                state =>
                {
                    state.LegalTask.ChangeDetails(
                        "Audit rollback changed",
                        null,
                        null,
                        null);
                    return LegalTaskMutationDecision.Persist;
                }));

        PostgresException postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal(
            "ck_audit_logs_details_contract",
            postgresException.ConstraintName);
        Assert.Equal(
            "Audit rollback original",
            (await FindTaskAsync(legalTask.Id)).Title);
        Assert.Empty(await FindAuditLogsAsync());
    }

    [Theory]
    [InlineData(AuditMutation.Assignee)]
    [InlineData(AuditMutation.Complete)]
    [InlineData(AuditMutation.Reopen)]
    public async Task MutationPersistence_AuditInsertFailure_RollsBackOtherMutations(
        AuditMutation mutation)
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        LegalTask legalTask = CreateTask(
            graph,
            null,
            graph.ActorMembership.Id,
            $"Audit rollback {mutation}",
            completed: mutation == AuditMutation.Reopen);
        await SeedAsync(legalTask);
        var request = new LegalTaskMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            legalTask.Id);
        string? invalidDetails = mutation == AuditMutation.Assignee
            ? null
            : "{}";

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => CreatePersistence(
                    new InvalidAuditDetailsInterceptor(invalidDetails))
                .ExecuteAsync(
                    request,
                    _ => mutation == AuditMutation.Assignee
                        ? graph.TargetMembership.Id
                        : null,
                    state =>
                    {
                        switch (mutation)
                        {
                            case AuditMutation.Assignee:
                                state.LegalTask.ChangeAssignee(
                                    graph.TargetMembership.Id);
                                break;
                            case AuditMutation.Complete:
                                state.LegalTask.Complete(TaskCompletedAt);
                                break;
                            case AuditMutation.Reopen:
                                state.LegalTask.Reopen();
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(nameof(mutation));
                        }

                        return LegalTaskMutationDecision.Persist;
                    }));

        PostgresException postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal(
            "ck_audit_logs_details_contract",
            postgresException.ConstraintName);
        LegalTask persisted = await FindTaskAsync(legalTask.Id);
        Assert.Null(persisted.AssigneeMembershipId);
        Assert.Equal(
            mutation == AuditMutation.Reopen ? TaskCompletedAt : null,
            persisted.CompletedAt);
        Assert.Empty(await FindAuditLogsAsync());
    }

    private UpdateLegalTaskUseCase CreateUpdateUseCase(EnmaDbContext queryContext)
    {
        return new UpdateLegalTaskUseCase(
            CreateAccessAuthorization(queryContext),
            new LegalTaskMutationAuthorization(),
            CreatePersistence());
    }

    private ChangeLegalTaskAssigneeUseCase CreateAssignmentUseCase(
        EnmaDbContext queryContext)
    {
        return new ChangeLegalTaskAssigneeUseCase(
            CreateAccessAuthorization(queryContext),
            new LegalTaskMutationAuthorization(),
            CreatePersistence());
    }

    private CompleteLegalTaskUseCase CreateCompleteUseCase(EnmaDbContext queryContext)
    {
        return new CompleteLegalTaskUseCase(
            CreateAccessAuthorization(queryContext),
            new LegalTaskMutationAuthorization(),
            CreatePersistence(),
            new FixedTimeProvider(TaskCompletedAt));
    }

    private ReopenLegalTaskUseCase CreateReopenUseCase(EnmaDbContext queryContext)
    {
        return new ReopenLegalTaskUseCase(
            CreateAccessAuthorization(queryContext),
            new LegalTaskMutationAuthorization(),
            CreatePersistence());
    }

    private static OrganizationAccessAuthorization CreateAccessAuthorization(
        EnmaDbContext queryContext)
    {
        return new OrganizationAccessAuthorization(
            new OrganizationAccessLookup(queryContext));
    }

    private LegalTaskMutationPersistence CreatePersistence(
        IInterceptor? interceptor = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString);

        if (interceptor is not null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        return new LegalTaskMutationPersistence(
            optionsBuilder.Options,
            new FixedTimeProvider(TaskCompletedAt));
    }

    private static Task<UpdateLegalTaskResult> UpdateTitleAsync(
        UpdateLegalTaskUseCase useCase,
        TenantGraph graph,
        Guid taskId,
        string title)
    {
        return useCase.ExecuteAsync(new UpdateLegalTaskCommand(
            graph.ActorUser.Id,
            graph.Organization.Id,
            taskId,
            title,
            null,
            null,
            null));
    }

    private static Task<UpdateLegalTaskResult> UpdateProcessAsync(
        UpdateLegalTaskUseCase useCase,
        TenantGraph graph,
        Guid taskId,
        Guid? processId,
        string title)
    {
        return useCase.ExecuteAsync(new UpdateLegalTaskCommand(
            graph.ActorUser.Id,
            graph.Organization.Id,
            taskId,
            title,
            null,
            null,
            processId));
    }

    private static Task<ChangeLegalTaskAssigneeResult> ChangeAssigneeAsync(
        ChangeLegalTaskAssigneeUseCase useCase,
        TenantGraph graph,
        Guid taskId,
        Guid? assigneeMembershipId)
    {
        return useCase.ExecuteAsync(new ChangeLegalTaskAssigneeCommand(
            graph.ActorUser.Id,
            graph.Organization.Id,
            taskId,
            assigneeMembershipId));
    }

    private async Task<TenantGraph> SeedTenantAsync(OrganizationRole actorRole)
    {
        var organization = new Organization(
            "Mutation organization",
            "mutation-organization",
            SeededAt);
        var actorUser = new User("Actor", "mutation-actor@example.test", SeededAt);
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actorUser.Id,
            actorRole,
            SeededAt);
        var targetUser = new User("Target", "mutation-target@example.test", SeededAt);
        var targetMembership = new OrganizationMembership(
            organization.Id,
            targetUser.Id,
            OrganizationRole.Member,
            SeededAt);
        await SeedAsync(
            organization,
            actorUser,
            actorMembership,
            targetUser,
            targetMembership);

        return new TenantGraph(
            organization,
            actorUser,
            actorMembership,
            targetUser,
            targetMembership);
    }

    private static LegalTask CreateTask(
        TenantGraph graph,
        Guid? assigneeMembershipId,
        Guid createdByMembershipId,
        string title = "Original task",
        bool completed = false)
    {
        var legalTask = new LegalTask(
            graph.Organization.Id,
            title,
            null,
            null,
            null,
            assigneeMembershipId,
            createdByMembershipId,
            TaskCreatedAt);

        if (completed)
        {
            legalTask.Complete(TaskCompletedAt);
        }

        return legalTask;
    }

    private async Task<LegalTask> FindTaskAsync(Guid legalTaskId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalTasks
            .AsNoTracking()
            .SingleAsync(task => task.Id == legalTaskId);
    }

    private async Task<AuditLog[]> FindAuditLogsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuditLogs.AsNoTracking().ToArrayAsync();
    }

    private async Task DeactivateUserAsync(Guid userId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = await dbContext.Users.SingleAsync(candidate => candidate.Id == userId);
        user.Deactivate();
        await dbContext.SaveChangesAsync();
    }

    private async Task UpdateMembershipAsync(OrganizationMembership membership)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.OrganizationMemberships.Update(membership);
        await dbContext.SaveChangesAsync();
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    public enum AssigneeAvailability
    {
        Missing = 0,
        CrossTenant = 1,
        InactiveMembership = 2,
        InactiveUser = 3
    }

    public enum AuditMutation
    {
        Assignee = 0,
        Complete = 1,
        Reopen = 2
    }

    private sealed record TenantGraph(
        Organization Organization,
        User ActorUser,
        OrganizationMembership ActorMembership,
        User TargetUser,
        OrganizationMembership TargetMembership);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class InvalidAuditDetailsInterceptor(string? detailsJson)
        : SaveChangesInterceptor
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
                .CurrentValue = detailsJson;
            return ValueTask.FromResult(result);
        }
    }
}
