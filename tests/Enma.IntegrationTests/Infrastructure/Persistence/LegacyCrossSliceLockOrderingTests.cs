using System.Data.Common;
using Enma.Application.Authorization;
using Enma.Application.Clients;
using Enma.Application.Deadlines.Create;
using Enma.Application.Deadlines.Reopen;
using Enma.Application.Deadlines.Responsible;
using Enma.Application.Organizations.Members.Lifecycle;
using Enma.Application.Organizations.Members.Role;
using Enma.Application.Organizations.UpdateName;
using Enma.Application.Processes;
using Enma.Application.Processes.Create;
using Enma.Application.Processes.Details;
using Enma.Application.Processes.Responsible;
using Enma.Application.Processes.Status;
using Enma.Application.Tasks;
using Enma.Application.Tasks.Assignment;
using Enma.Domain.Auditing;
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

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegacyCrossSliceLockOrderingTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        29,
        12,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ClientCreationAndOrganizationRename_SerializeWithoutPartialWrites()
    {
        TestGraph graph = await SeedGraphAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();

        Task<ClientCreationPersistenceResult> creation =
            CreateClientPersistence(
                    new PauseAfterMembershipLockInterceptor(gate))
                .ExecuteAsync(
                    new ClientCreationPersistenceRequest(
                        graph.ActorUser.Id,
                        graph.Organization.Id,
                        graph.ActorMembership.Id),
                    state => state.IsOrganizationActive &&
                        state.Actor?.IsAvailableFor(
                            graph.ActorUser.Id,
                            graph.Organization.Id,
                            graph.ActorMembership.Id) == true
                            ? ClientCreationDecision.Persist(
                                new Client(
                                    graph.Organization.Id,
                                    "Deadlock Client",
                                    Now))
                            : ClientCreationDecision.AccessDenied,
                    timeout.Token);

        await gate.MembershipLocked.WaitAsync(timeout.Token);

        Task<OrganizationNameMutationPersistenceResult> rename =
            CreateRenamePersistence(
                    new SignalAfterOrganizationLockInterceptor(gate))
                .ExecuteAsync(
                    new OrganizationNameMutationPersistenceRequest(
                        graph.ActorUser.Id,
                        graph.Organization.Id,
                        graph.ActorMembership.Id,
                        "Renamed Organization"),
                    timeout.Token);

        try
        {
            await gate.OrganizationLocked.WaitAsync(timeout.Token);
            gate.ReleaseCreation();

            ClientCreationPersistenceResult creationResult =
                await creation.WaitAsync(timeout.Token);
            OrganizationNameMutationPersistenceResult renameResult =
                await rename.WaitAsync(timeout.Token);

            Assert.Equal(
                ClientCreationDecisionStatus.Persist,
                creationResult.Status);
            Assert.Equal(
                OrganizationNameMutationPersistenceResult.Succeeded,
                renameResult);

            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(
                "Renamed Organization",
                await dbContext.Organizations
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == graph.Organization.Id)
                    .Select(candidate => candidate.Name)
                    .SingleAsync(timeout.Token));
            Assert.Equal(
                1,
                await dbContext.Clients.CountAsync(
                    client => client.OrganizationId == graph.Organization.Id,
                    timeout.Token));
            Assert.Equal(
                new[]
                {
                    AuditEventType.OrganizationRenamed,
                    AuditEventType.ClientCreated
                }.OrderBy(eventType => eventType),
                await dbContext.AuditLogs
                    .AsNoTracking()
                    .Where(auditLog =>
                        auditLog.OrganizationId == graph.Organization.Id)
                    .Select(auditLog => auditLog.EventType)
                    .OrderBy(eventType => eventType)
                    .ToListAsync(timeout.Token));
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(creation);
            await DrainAsync(rename);
        }
    }

    [Fact]
    public async Task ClientCreationAndMemberRoleChange_SerializeWithoutPartialWrites()
    {
        TestGraph graph = await SeedGraphAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        Task<ClientCreationPersistenceResult> creation = StartPausedClientCreation(
            graph,
            "Role Client",
            gate,
            timeout.Token);

        await gate.MembershipLocked.WaitAsync(timeout.Token);

        Task<OrganizationMemberRoleMutationPersistenceResult> roleChange =
            CreateRolePersistence(
                    new SignalAfterOrganizationLockInterceptor(gate))
                .ExecuteAsync(
                    new OrganizationMemberRoleMutationPersistenceRequest(
                        graph.ActorUser.Id,
                        graph.Organization.Id,
                        graph.ActorMembership.Id,
                        graph.TargetMembership.Id,
                        OrganizationRole.Administrator,
                        OrganizationRole.Member),
                    timeout.Token);

        try
        {
            await gate.OrganizationLocked.WaitAsync(timeout.Token);
            gate.ReleaseCreation();

            Assert.Equal(
                ClientCreationDecisionStatus.Persist,
                (await creation.WaitAsync(timeout.Token)).Status);
            Assert.Equal(
                OrganizationMemberRoleMutationPersistenceResult.Succeeded,
                await roleChange.WaitAsync(timeout.Token));

            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(
                OrganizationRole.Administrator,
                await dbContext.OrganizationMemberships
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == graph.TargetMembership.Id)
                    .Select(candidate => candidate.Role)
                    .SingleAsync(timeout.Token));
            Assert.Equal(1, await dbContext.Clients.CountAsync(timeout.Token));
            Assert.Equal(
                new[]
                {
                    AuditEventType.OrganizationMembershipRoleChanged,
                    AuditEventType.ClientCreated
                }.OrderBy(eventType => eventType),
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(creation);
            await DrainAsync(roleChange);
        }
    }

    [Fact]
    public async Task ClientCreationAndMemberLifecycle_SerializeWithoutPartialWrites()
    {
        TestGraph graph = await SeedGraphAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        Task<ClientCreationPersistenceResult> creation = StartPausedClientCreation(
            graph,
            "Lifecycle Client",
            gate,
            timeout.Token);

        await gate.MembershipLocked.WaitAsync(timeout.Token);

        Task<OrganizationMemberLifecycleMutationPersistenceResult> lifecycle =
            CreateLifecyclePersistence(
                    new SignalAfterOrganizationLockInterceptor(gate))
                .ExecuteAsync(
                    new OrganizationMemberLifecycleMutationPersistenceRequest(
                        graph.ActorUser.Id,
                        graph.Organization.Id,
                        graph.ActorMembership.Id,
                        graph.TargetMembership.Id,
                        OrganizationMemberLifecycleOperation.Deactivate),
                    timeout.Token);

        try
        {
            await gate.OrganizationLocked.WaitAsync(timeout.Token);
            gate.ReleaseCreation();

            Assert.Equal(
                ClientCreationDecisionStatus.Persist,
                (await creation.WaitAsync(timeout.Token)).Status);
            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
                await lifecycle.WaitAsync(timeout.Token));

            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.False(await dbContext.OrganizationMemberships
                .AsNoTracking()
                .Where(candidate => candidate.Id == graph.TargetMembership.Id)
                .Select(candidate => candidate.IsActive)
                .SingleAsync(timeout.Token));
            Assert.Equal(1, await dbContext.Clients.CountAsync(timeout.Token));
            Assert.Equal(
                new[]
                {
                    AuditEventType.OrganizationMembershipDeactivated,
                    AuditEventType.ClientCreated
                }.OrderBy(eventType => eventType),
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(creation);
            await DrainAsync(lifecycle);
        }
    }

    [Fact]
    public async Task LegalTaskCreationAndProcessMutation_UseProcessBeforeIdentityOrder()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalProcess legalProcess = await SeedProcessAsync(graph);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new ProcessLockGate();
        Task<LegalProcessMutationPersistenceResult> processMutation =
            StartPausedProcessMutation(
                graph,
                legalProcess,
                gate,
                timeout.Token);

        await gate.ProcessLocked.WaitAsync(timeout.Token);

        Task<LegalTaskCreationPersistenceResult> taskCreation =
            CreateTaskPersistence().ExecuteAsync(
                new LegalTaskCreationPersistenceRequest(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    graph.ActorMembership.Id,
                    null,
                    legalProcess.Id),
                state => state.Actor?.IsMembershipActive == true &&
                    state.Actor.IsUserActive
                        ? LegalTaskCreationDecision.Persist(
                            new LegalTask(
                                graph.Organization.Id,
                                "Serialized Task",
                                null,
                                null,
                                legalProcess.Id,
                                null,
                                graph.ActorMembership.Id,
                                Now))
                        : LegalTaskCreationDecision.AccessDenied,
                timeout.Token);

        try
        {
            await WaitForBlockedProcessLockAsync(timeout.Token);
            Assert.False(taskCreation.IsCompleted);
            gate.ReleaseMutation();

            Assert.Equal(
                LegalProcessMutationPersistenceResult.Updated,
                await processMutation.WaitAsync(timeout.Token));
            Assert.Equal(
                LegalTaskCreationDecisionStatus.Persist,
                (await taskCreation.WaitAsync(timeout.Token)).Status);

            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(
                "Serialized Process",
                await dbContext.LegalProcesses
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == legalProcess.Id)
                    .Select(candidate => candidate.Title)
                    .SingleAsync(timeout.Token));
            Assert.Equal(
                1,
                await dbContext.LegalTasks.CountAsync(
                    legalTask => legalTask.ProcessId == legalProcess.Id,
                    timeout.Token));
            Assert.Equal(
                new[]
                {
                    AuditEventType.LegalProcessTitleChanged,
                    AuditEventType.LegalTaskCreated
                }.OrderBy(eventType => eventType),
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseMutation();
            await DrainAsync(processMutation);
            await DrainAsync(taskCreation);
        }
    }

    [Fact]
    public async Task LegalTaskUpdateAndProcessMutation_UseRetryBeforeProcessLock()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalProcess legalProcess = await SeedProcessAsync(graph);
        LegalTask legalTask = await SeedLegalTaskAsync(graph);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new ProcessLockGate();
        Task<LegalProcessMutationPersistenceResult> processMutation =
            StartPausedProcessMutation(
                graph,
                legalProcess,
                gate,
                timeout.Token);

        await gate.ProcessLocked.WaitAsync(timeout.Token);

        Task<LegalTaskMutationPersistenceResult> taskMutation =
            CreateTaskMutationPersistence().ExecuteAsync(
                new LegalTaskMutationPersistenceRequest(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    graph.ActorMembership.Id,
                    legalTask.Id),
                _ => null,
                state =>
                {
                    if (state.Actor?.IsMembershipActive != true ||
                        !state.Actor.IsUserActive)
                    {
                        return LegalTaskMutationDecision.AccessDenied;
                    }

                    if (state.ValidatedProcessId != legalProcess.Id)
                    {
                        return LegalTaskMutationDecision.ValidateProcess(
                            legalProcess.Id);
                    }

                    if (state.IsProcessAvailable != true)
                    {
                        return LegalTaskMutationDecision
                            .RelatedProcessUnavailable;
                    }

                    state.LegalTask.ChangeDetails(
                        "Task With Process",
                        null,
                        null,
                        legalProcess.Id);
                    return LegalTaskMutationDecision.Persist;
                },
                timeout.Token);

        try
        {
            await WaitForBlockedProcessLockAsync(timeout.Token);
            Assert.False(taskMutation.IsCompleted);
            gate.ReleaseMutation();

            Assert.Equal(
                LegalProcessMutationPersistenceResult.Updated,
                await processMutation.WaitAsync(timeout.Token));
            Assert.Equal(
                LegalTaskMutationPersistenceResult.Succeeded,
                await taskMutation.WaitAsync(timeout.Token));

            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(
                legalProcess.Id,
                await dbContext.LegalTasks
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == legalTask.Id)
                    .Select(candidate => candidate.ProcessId)
                    .SingleAsync(timeout.Token));
            Assert.Equal(
                "Serialized Process",
                await dbContext.LegalProcesses
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == legalProcess.Id)
                    .Select(candidate => candidate.Title)
                    .SingleAsync(timeout.Token));
            Assert.Equal(
                new[]
                {
                    AuditEventType.LegalProcessTitleChanged,
                    AuditEventType.LegalTaskDetailsChanged
                }.OrderBy(eventType => eventType),
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseMutation();
            await DrainAsync(processMutation);
            await DrainAsync(taskMutation);
        }
    }

    [Fact]
    public async Task ProcessResponsibleAssignmentFirst_BlocksMemberDeactivation()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalProcess legalProcess = await SeedProcessAsync(graph);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        Task<ChangeLegalProcessResponsibleResult> assignment = ChangeResponsibleAsync(
            graph,
            legalProcess.Id,
            graph.TargetMembership.Id,
            new PauseAfterMembershipLockInterceptor(gate),
            timeout.Token);

        await gate.MembershipLocked.WaitAsync(timeout.Token);

        Task<OrganizationMemberLifecycleMutationPersistenceResult> lifecycle =
            StartTargetDeactivation(
                graph,
                new SignalAfterOrganizationLockInterceptor(gate),
                timeout.Token);

        try
        {
            await gate.OrganizationLocked.WaitAsync(timeout.Token);
            gate.ReleaseCreation();

            Assert.Equal(
                ChangeLegalProcessResponsibleResult.Succeeded,
                await assignment.WaitAsync(timeout.Token));
            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult
                    .ActiveAssignmentsConflict,
                await lifecycle.WaitAsync(timeout.Token));

            await AssertOpenProcessesHaveAvailableResponsibleAsync(
                graph,
                timeout.Token);
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(
                graph.TargetMembership.Id,
                await GetResponsibleAsync(dbContext, legalProcess.Id, timeout.Token));
            Assert.Equal(
                new[] { AuditEventType.LegalProcessResponsibleChanged },
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(assignment);
            await DrainAsync(lifecycle);
        }
    }

    [Fact]
    public async Task MemberDeactivationFirst_RejectsProcessResponsibleAssignment()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalProcess legalProcess = await SeedProcessAsync(graph);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        Task<OrganizationMemberLifecycleMutationPersistenceResult> lifecycle =
            StartTargetDeactivation(
                graph,
                new PauseAfterMembershipLockInterceptor(gate),
                timeout.Token);

        await gate.MembershipLocked.WaitAsync(timeout.Token);

        Task<ChangeLegalProcessResponsibleResult> assignment = ChangeResponsibleAsync(
            graph,
            legalProcess.Id,
            graph.TargetMembership.Id,
            interceptor: null,
            timeout.Token);

        try
        {
            await WaitForBlockedMembershipLockAsync(timeout.Token);
            Assert.False(assignment.IsCompleted);
            gate.ReleaseCreation();

            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
                await lifecycle.WaitAsync(timeout.Token));
            Assert.Equal(
                ChangeLegalProcessResponsibleResult.RelatedResponsibleUnavailable,
                await assignment.WaitAsync(timeout.Token));

            await AssertOpenProcessesHaveAvailableResponsibleAsync(
                graph,
                timeout.Token);
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Null(await GetResponsibleAsync(
                dbContext,
                legalProcess.Id,
                timeout.Token));
            Assert.Equal(
                new[] { AuditEventType.OrganizationMembershipDeactivated },
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(lifecycle);
            await DrainAsync(assignment);
        }
    }

    [Fact]
    public async Task ProcessReopenFirst_BlocksDeactivationOfCurrentResponsible()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalProcess legalProcess = await SeedProcessAsync(
            graph,
            graph.TargetMembership.Id,
            LegalProcessStatus.Closed);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        Task<ChangeLegalProcessStatusResult> reopen = ChangeStatusAsync(
            graph,
            legalProcess.Id,
            "inProgress",
            new PauseAfterMembershipLockInterceptor(gate),
            timeout.Token);

        await gate.MembershipLocked.WaitAsync(timeout.Token);

        Task<OrganizationMemberLifecycleMutationPersistenceResult> lifecycle =
            StartTargetDeactivation(
                graph,
                new SignalAfterOrganizationLockInterceptor(gate),
                timeout.Token);

        try
        {
            await gate.OrganizationLocked.WaitAsync(timeout.Token);
            gate.ReleaseCreation();

            Assert.Equal(
                ChangeLegalProcessStatusResult.Succeeded,
                await reopen.WaitAsync(timeout.Token));
            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult
                    .ActiveAssignmentsConflict,
                await lifecycle.WaitAsync(timeout.Token));

            await AssertOpenProcessesHaveAvailableResponsibleAsync(
                graph,
                timeout.Token);
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(
                new[] { AuditEventType.LegalProcessStatusChanged },
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(reopen);
            await DrainAsync(lifecycle);
        }
    }

    [Fact]
    public async Task ProcessCreationWithResponsibleFirst_BlocksMemberDeactivation()
    {
        TestGraph graph = await SeedGraphAsync();
        Client client = await SeedClientAsync(graph);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        Task<CreateLegalProcessResult> creation = CreateProcessAsync(
            graph,
            client.Id,
            graph.TargetMembership.Id,
            new PauseAfterMembershipLockInterceptor(gate),
            timeout.Token);

        await gate.MembershipLocked.WaitAsync(timeout.Token);

        Task<OrganizationMemberLifecycleMutationPersistenceResult> lifecycle =
            StartTargetDeactivation(
                graph,
                new SignalAfterOrganizationLockInterceptor(gate),
                timeout.Token);

        try
        {
            await gate.OrganizationLocked.WaitAsync(timeout.Token);
            gate.ReleaseCreation();

            CreateLegalProcessResult creationResult =
                await creation.WaitAsync(timeout.Token);
            Assert.Equal(
                CreateLegalProcessResultStatus.Succeeded,
                creationResult.Status);
            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult
                    .ActiveAssignmentsConflict,
                await lifecycle.WaitAsync(timeout.Token));

            await AssertOpenProcessesHaveAvailableResponsibleAsync(
                graph,
                timeout.Token);
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(
                graph.TargetMembership.Id,
                await GetResponsibleAsync(
                    dbContext,
                    Assert.IsType<Guid>(creationResult.ProcessId),
                    timeout.Token));
            Assert.True(await dbContext.OrganizationMemberships
                .AsNoTracking()
                .Where(candidate => candidate.Id == graph.TargetMembership.Id)
                .Select(candidate => candidate.IsActive)
                .SingleAsync(timeout.Token));
            Assert.Equal(
                new[] { AuditEventType.LegalProcessCreated },
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(creation);
            await DrainAsync(lifecycle);
        }
    }

    [Fact]
    public async Task MemberDeactivationFirst_RejectsProcessCreationWithResponsible()
    {
        TestGraph graph = await SeedGraphAsync();
        Client client = await SeedClientAsync(graph);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        Task<OrganizationMemberLifecycleMutationPersistenceResult> lifecycle =
            StartTargetDeactivation(
                graph,
                new PauseAfterMembershipLockInterceptor(gate),
                timeout.Token);

        await gate.MembershipLocked.WaitAsync(timeout.Token);

        Task<CreateLegalProcessResult> creation = CreateProcessAsync(
            graph,
            client.Id,
            graph.TargetMembership.Id,
            interceptor: null,
            timeout.Token);

        try
        {
            await WaitForBlockedMembershipLockAsync(timeout.Token);
            Assert.False(creation.IsCompleted);
            gate.ReleaseCreation();

            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
                await lifecycle.WaitAsync(timeout.Token));
            Assert.Same(
                CreateLegalProcessResult.RelatedResponsibleUnavailable,
                await creation.WaitAsync(timeout.Token));

            await AssertOpenProcessesHaveAvailableResponsibleAsync(
                graph,
                timeout.Token);
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.False(await dbContext.LegalProcesses.AnyAsync(timeout.Token));
            Assert.Equal(
                new[] { AuditEventType.OrganizationMembershipDeactivated },
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(lifecycle);
            await DrainAsync(creation);
        }
    }

    [Fact]
    public async Task ProcessCreationWithResponsibleFirst_SerializesResponsibleAssignment()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalProcess legalProcess = await SeedProcessAsync(graph);
        Client client = await SeedClientAsync(graph);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        Task<CreateLegalProcessResult> creation = CreateProcessAsync(
            graph,
            client.Id,
            graph.TargetMembership.Id,
            new PauseAfterMembershipLockInterceptor(gate),
            timeout.Token);

        await gate.MembershipLocked.WaitAsync(timeout.Token);

        Task<ChangeLegalProcessResponsibleResult> assignment = ChangeResponsibleAsync(
            graph,
            legalProcess.Id,
            graph.TargetMembership.Id,
            interceptor: null,
            timeout.Token);

        try
        {
            await WaitForBlockedMembershipLockAsync(timeout.Token);
            Assert.False(assignment.IsCompleted);
            gate.ReleaseCreation();

            CreateLegalProcessResult creationResult =
                await creation.WaitAsync(timeout.Token);
            Assert.Equal(
                CreateLegalProcessResultStatus.Succeeded,
                creationResult.Status);
            Assert.Equal(
                ChangeLegalProcessResponsibleResult.Succeeded,
                await assignment.WaitAsync(timeout.Token));

            await AssertCreationAndAssignmentPersistedAsync(
                graph,
                legalProcess.Id,
                Assert.IsType<Guid>(creationResult.ProcessId),
                timeout.Token);
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(creation);
            await DrainAsync(assignment);
        }
    }

    [Fact]
    public async Task ResponsibleAssignmentFirst_SerializesProcessCreationWithResponsible()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalProcess legalProcess = await SeedProcessAsync(graph);
        Client client = await SeedClientAsync(graph);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        Task<ChangeLegalProcessResponsibleResult> assignment = ChangeResponsibleAsync(
            graph,
            legalProcess.Id,
            graph.TargetMembership.Id,
            new PauseAfterMembershipLockInterceptor(gate),
            timeout.Token);

        await gate.MembershipLocked.WaitAsync(timeout.Token);

        Task<CreateLegalProcessResult> creation = CreateProcessAsync(
            graph,
            client.Id,
            graph.TargetMembership.Id,
            interceptor: null,
            timeout.Token);

        try
        {
            await WaitForBlockedMembershipLockAsync(timeout.Token);
            Assert.False(creation.IsCompleted);
            gate.ReleaseCreation();

            Assert.Equal(
                ChangeLegalProcessResponsibleResult.Succeeded,
                await assignment.WaitAsync(timeout.Token));
            CreateLegalProcessResult creationResult =
                await creation.WaitAsync(timeout.Token);
            Assert.Equal(
                CreateLegalProcessResultStatus.Succeeded,
                creationResult.Status);

            await AssertCreationAndAssignmentPersistedAsync(
                graph,
                legalProcess.Id,
                Assert.IsType<Guid>(creationResult.ProcessId),
                timeout.Token);
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(assignment);
            await DrainAsync(creation);
        }
    }

    [Fact]
    public async Task LegalTaskUpdateAndProcessOperationalMutation_UseRetryBeforeProcessLock()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalProcess legalProcess = await SeedProcessAsync(graph);
        LegalTask legalTask = await SeedLegalTaskAsync(graph);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new ProcessLockGate();
        Task<ChangeLegalProcessDetailsResult> processMutation = ChangeDetailsAsync(
            graph,
            legalProcess.Id,
            new PauseAfterProcessLockInterceptor(gate),
            timeout.Token);

        await gate.ProcessLocked.WaitAsync(timeout.Token);

        Task<LegalTaskMutationPersistenceResult> taskMutation =
            CreateTaskMutationPersistence().ExecuteAsync(
                new LegalTaskMutationPersistenceRequest(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    graph.ActorMembership.Id,
                    legalTask.Id),
                _ => null,
                state =>
                {
                    if (state.Actor?.IsMembershipActive != true ||
                        !state.Actor.IsUserActive)
                    {
                        return LegalTaskMutationDecision.AccessDenied;
                    }

                    if (state.ValidatedProcessId != legalProcess.Id)
                    {
                        return LegalTaskMutationDecision.ValidateProcess(
                            legalProcess.Id);
                    }

                    if (state.IsProcessAvailable != true)
                    {
                        return LegalTaskMutationDecision
                            .RelatedProcessUnavailable;
                    }

                    state.LegalTask.ChangeDetails(
                        "Task With Process",
                        null,
                        null,
                        legalProcess.Id);
                    return LegalTaskMutationDecision.Persist;
                },
                timeout.Token);

        try
        {
            await WaitForBlockedProcessLockAsync(timeout.Token);
            Assert.False(taskMutation.IsCompleted);
            gate.ReleaseMutation();

            Assert.Equal(
                ChangeLegalProcessDetailsResult.Succeeded,
                await processMutation.WaitAsync(timeout.Token));
            Assert.Equal(
                LegalTaskMutationPersistenceResult.Succeeded,
                await taskMutation.WaitAsync(timeout.Token));

            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(
                "Serialized Number",
                await dbContext.LegalProcesses
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == legalProcess.Id)
                    .Select(candidate => candidate.ProcessNumber)
                    .SingleAsync(timeout.Token));
            Assert.Equal(
                legalProcess.Id,
                await dbContext.LegalTasks
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == legalTask.Id)
                    .Select(candidate => candidate.ProcessId)
                    .SingleAsync(timeout.Token));
            Assert.Equal(
                new[]
                {
                    AuditEventType.LegalTaskDetailsChanged,
                    AuditEventType.LegalProcessDetailsChanged
                }.OrderBy(eventType => eventType),
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseMutation();
            await DrainAsync(processMutation);
            await DrainAsync(taskMutation);
        }
    }

    [Fact]
    public async Task DeadlineResponsibleAssignmentFirst_BlocksMemberDeactivation()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalDeadline legalDeadline = await SeedDeadlineAsync(graph);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        Task<ChangeLegalDeadlineResponsibleResult> assignment =
            ChangeDeadlineResponsibleAsync(
                graph,
                legalDeadline.Id,
                graph.TargetMembership.Id,
                new PauseAfterMembershipLockInterceptor(gate),
                timeout.Token);

        await gate.MembershipLocked.WaitAsync(timeout.Token);

        Task<OrganizationMemberLifecycleMutationPersistenceResult> lifecycle =
            StartTargetDeactivation(
                graph,
                new SignalAfterOrganizationLockInterceptor(gate),
                timeout.Token);

        try
        {
            await gate.OrganizationLocked.WaitAsync(timeout.Token);
            gate.ReleaseCreation();

            Assert.Equal(
                ChangeLegalDeadlineResponsibleResult.Succeeded,
                await assignment.WaitAsync(timeout.Token));
            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult
                    .ActiveAssignmentsConflict,
                await lifecycle.WaitAsync(timeout.Token));

            await AssertPendingDeadlinesHaveAvailableResponsibleAsync(
                graph,
                timeout.Token);
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(
                graph.TargetMembership.Id,
                await GetDeadlineResponsibleAsync(
                    dbContext,
                    legalDeadline.Id,
                    timeout.Token));
            Assert.Equal(
                new[] { AuditEventType.LegalDeadlineResponsibleChanged },
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(assignment);
            await DrainAsync(lifecycle);
        }
    }

    [Fact]
    public async Task MemberDeactivationFirst_RejectsDeadlineResponsibleAssignment()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalDeadline legalDeadline = await SeedDeadlineAsync(graph);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        Task<OrganizationMemberLifecycleMutationPersistenceResult> lifecycle =
            StartTargetDeactivation(
                graph,
                new PauseAfterMembershipLockInterceptor(gate),
                timeout.Token);

        await gate.MembershipLocked.WaitAsync(timeout.Token);

        Task<ChangeLegalDeadlineResponsibleResult> assignment =
            ChangeDeadlineResponsibleAsync(
                graph,
                legalDeadline.Id,
                graph.TargetMembership.Id,
                interceptor: null,
                timeout.Token);

        try
        {
            await WaitForBlockedMembershipLockAsync(timeout.Token);
            Assert.False(assignment.IsCompleted);
            gate.ReleaseCreation();

            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
                await lifecycle.WaitAsync(timeout.Token));
            Assert.Equal(
                ChangeLegalDeadlineResponsibleResult.RelatedResponsibleUnavailable,
                await assignment.WaitAsync(timeout.Token));

            await AssertPendingDeadlinesHaveAvailableResponsibleAsync(
                graph,
                timeout.Token);
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Null(await GetDeadlineResponsibleAsync(
                dbContext,
                legalDeadline.Id,
                timeout.Token));
            Assert.Equal(
                new[] { AuditEventType.OrganizationMembershipDeactivated },
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(lifecycle);
            await DrainAsync(assignment);
        }
    }

    [Fact]
    public async Task DeadlineCreationWithResponsibleFirst_BlocksMemberDeactivation()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalProcess legalProcess = await SeedProcessAsync(graph);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        Task<CreateLegalDeadlineResult> creation = CreateDeadlineAsync(
            graph,
            legalProcess.Id,
            graph.TargetMembership.Id,
            new PauseAfterMembershipLockInterceptor(gate),
            timeout.Token);

        await gate.MembershipLocked.WaitAsync(timeout.Token);

        Task<OrganizationMemberLifecycleMutationPersistenceResult> lifecycle =
            StartTargetDeactivation(
                graph,
                new SignalAfterOrganizationLockInterceptor(gate),
                timeout.Token);

        try
        {
            await gate.OrganizationLocked.WaitAsync(timeout.Token);
            gate.ReleaseCreation();

            CreateLegalDeadlineResult creationResult =
                await creation.WaitAsync(timeout.Token);
            Assert.Equal(
                CreateLegalDeadlineResultStatus.Created,
                creationResult.Status);
            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult
                    .ActiveAssignmentsConflict,
                await lifecycle.WaitAsync(timeout.Token));

            await AssertPendingDeadlinesHaveAvailableResponsibleAsync(
                graph,
                timeout.Token);
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(
                graph.TargetMembership.Id,
                await GetDeadlineResponsibleAsync(
                    dbContext,
                    Assert.IsType<Guid>(creationResult.DeadlineId),
                    timeout.Token));
            Assert.Equal(
                new[] { AuditEventType.LegalDeadlineCreated },
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(creation);
            await DrainAsync(lifecycle);
        }
    }

    [Fact]
    public async Task MemberDeactivationFirst_RejectsDeadlineCreationWithResponsible()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalProcess legalProcess = await SeedProcessAsync(graph);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        Task<OrganizationMemberLifecycleMutationPersistenceResult> lifecycle =
            StartTargetDeactivation(
                graph,
                new PauseAfterMembershipLockInterceptor(gate),
                timeout.Token);

        await gate.MembershipLocked.WaitAsync(timeout.Token);

        Task<CreateLegalDeadlineResult> creation = CreateDeadlineAsync(
            graph,
            legalProcess.Id,
            graph.TargetMembership.Id,
            interceptor: null,
            timeout.Token);

        try
        {
            await WaitForBlockedMembershipLockAsync(timeout.Token);
            Assert.False(creation.IsCompleted);
            gate.ReleaseCreation();

            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
                await lifecycle.WaitAsync(timeout.Token));
            Assert.Same(
                CreateLegalDeadlineResult.RelatedResponsibleUnavailable,
                await creation.WaitAsync(timeout.Token));

            await AssertPendingDeadlinesHaveAvailableResponsibleAsync(
                graph,
                timeout.Token);
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.False(await dbContext.LegalDeadlines.AnyAsync(timeout.Token));
            Assert.Equal(
                new[] { AuditEventType.OrganizationMembershipDeactivated },
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(lifecycle);
            await DrainAsync(creation);
        }
    }

    [Fact]
    public async Task DeadlineReopenFirst_BlocksDeactivationOfCurrentResponsible()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalDeadline legalDeadline = await SeedDeadlineAsync(
            graph,
            graph.TargetMembership.Id,
            completed: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        Task<ReopenLegalDeadlineResult> reopen = ReopenDeadlineAsync(
            graph,
            legalDeadline.Id,
            new PauseAfterMembershipLockInterceptor(gate),
            timeout.Token);

        await gate.MembershipLocked.WaitAsync(timeout.Token);

        Task<OrganizationMemberLifecycleMutationPersistenceResult> lifecycle =
            StartTargetDeactivation(
                graph,
                new SignalAfterOrganizationLockInterceptor(gate),
                timeout.Token);

        try
        {
            await gate.OrganizationLocked.WaitAsync(timeout.Token);
            gate.ReleaseCreation();

            Assert.Same(
                ReopenLegalDeadlineResult.Succeeded,
                await reopen.WaitAsync(timeout.Token));
            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult
                    .ActiveAssignmentsConflict,
                await lifecycle.WaitAsync(timeout.Token));

            await AssertPendingDeadlinesHaveAvailableResponsibleAsync(
                graph,
                timeout.Token);
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(
                new[] { AuditEventType.LegalDeadlineReopened },
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(reopen);
            await DrainAsync(lifecycle);
        }
    }

    [Fact]
    public async Task MemberDeactivationFirst_RejectsReopeningDeadlineOfResponsible()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalDeadline legalDeadline = await SeedDeadlineAsync(
            graph,
            graph.TargetMembership.Id,
            completed: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        Task<OrganizationMemberLifecycleMutationPersistenceResult> lifecycle =
            StartTargetDeactivation(
                graph,
                new PauseAfterMembershipLockInterceptor(gate),
                timeout.Token);

        await gate.MembershipLocked.WaitAsync(timeout.Token);

        Task<ReopenLegalDeadlineResult> reopen = ReopenDeadlineAsync(
            graph,
            legalDeadline.Id,
            interceptor: null,
            timeout.Token);

        try
        {
            await WaitForBlockedMembershipLockAsync(timeout.Token);
            Assert.False(reopen.IsCompleted);
            gate.ReleaseCreation();

            Assert.Equal(
                OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
                await lifecycle.WaitAsync(timeout.Token));
            Assert.Same(
                ReopenLegalDeadlineResult.CurrentResponsibleUnavailable,
                await reopen.WaitAsync(timeout.Token));

            await AssertPendingDeadlinesHaveAvailableResponsibleAsync(
                graph,
                timeout.Token);
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.NotNull(await dbContext.LegalDeadlines
                .AsNoTracking()
                .Where(candidate => candidate.Id == legalDeadline.Id)
                .Select(candidate => candidate.CompletedAt)
                .SingleAsync(timeout.Token));
            Assert.Equal(
                new[] { AuditEventType.OrganizationMembershipDeactivated },
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(lifecycle);
            await DrainAsync(reopen);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeadlineAndProcessResponsibleMutations_SerializeOnSharedMembershipsWithoutDeadlock(
        bool deadlineFirst)
    {
        TestGraph graph = await SeedGraphAsync();
        LegalDeadline legalDeadline = await SeedDeadlineAsync(graph);
        LegalProcess legalProcess = await SeedProcessAsync(graph);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        var pause = new PauseAfterMembershipLockInterceptor(gate);
        Task<ChangeLegalDeadlineResponsibleResult> deadlineMutation;
        Task<ChangeLegalProcessResponsibleResult> processMutation;

        if (deadlineFirst)
        {
            deadlineMutation = ChangeDeadlineResponsibleAsync(
                graph,
                legalDeadline.Id,
                graph.TargetMembership.Id,
                pause,
                timeout.Token);
            await gate.MembershipLocked.WaitAsync(timeout.Token);
            processMutation = ChangeResponsibleAsync(
                graph,
                legalProcess.Id,
                graph.TargetMembership.Id,
                interceptor: null,
                timeout.Token);
        }
        else
        {
            processMutation = ChangeResponsibleAsync(
                graph,
                legalProcess.Id,
                graph.TargetMembership.Id,
                pause,
                timeout.Token);
            await gate.MembershipLocked.WaitAsync(timeout.Token);
            deadlineMutation = ChangeDeadlineResponsibleAsync(
                graph,
                legalDeadline.Id,
                graph.TargetMembership.Id,
                interceptor: null,
                timeout.Token);
        }

        try
        {
            await WaitForBlockedMembershipLockAsync(timeout.Token);
            Assert.False(deadlineFirst ? processMutation.IsCompleted : deadlineMutation.IsCompleted);
            gate.ReleaseCreation();

            Assert.Equal(
                ChangeLegalDeadlineResponsibleResult.Succeeded,
                await deadlineMutation.WaitAsync(timeout.Token));
            Assert.Equal(
                ChangeLegalProcessResponsibleResult.Succeeded,
                await processMutation.WaitAsync(timeout.Token));

            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(
                graph.TargetMembership.Id,
                await GetDeadlineResponsibleAsync(
                    dbContext,
                    legalDeadline.Id,
                    timeout.Token));
            Assert.Equal(
                graph.TargetMembership.Id,
                await GetResponsibleAsync(dbContext, legalProcess.Id, timeout.Token));
            Assert.Equal(
                new[]
                {
                    AuditEventType.LegalProcessResponsibleChanged,
                    AuditEventType.LegalDeadlineResponsibleChanged
                }.OrderBy(eventType => eventType),
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(deadlineMutation);
            await DrainAsync(processMutation);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeadlineResponsibleAndTaskAssigneeMutations_SerializeOnSharedMembershipsWithoutDeadlock(
        bool deadlineFirst)
    {
        TestGraph graph = await SeedGraphAsync();
        LegalDeadline legalDeadline = await SeedDeadlineAsync(graph);
        LegalTask legalTask = await SeedLegalTaskAsync(graph);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new CrossSliceLockGate();
        var pause = new PauseAfterMembershipLockInterceptor(gate);
        Task<ChangeLegalDeadlineResponsibleResult> deadlineMutation;
        Task<ChangeLegalTaskAssigneeResult> taskMutation;

        if (deadlineFirst)
        {
            deadlineMutation = ChangeDeadlineResponsibleAsync(
                graph,
                legalDeadline.Id,
                graph.TargetMembership.Id,
                pause,
                timeout.Token);
            await gate.MembershipLocked.WaitAsync(timeout.Token);
            taskMutation = ChangeTaskAssigneeAsync(
                graph,
                legalTask.Id,
                graph.TargetMembership.Id,
                interceptor: null,
                timeout.Token);
        }
        else
        {
            taskMutation = ChangeTaskAssigneeAsync(
                graph,
                legalTask.Id,
                graph.TargetMembership.Id,
                pause,
                timeout.Token);
            await gate.MembershipLocked.WaitAsync(timeout.Token);
            deadlineMutation = ChangeDeadlineResponsibleAsync(
                graph,
                legalDeadline.Id,
                graph.TargetMembership.Id,
                interceptor: null,
                timeout.Token);
        }

        try
        {
            await WaitForBlockedMembershipLockAsync(timeout.Token);
            Assert.False(deadlineFirst ? taskMutation.IsCompleted : deadlineMutation.IsCompleted);
            gate.ReleaseCreation();

            Assert.Equal(
                ChangeLegalDeadlineResponsibleResult.Succeeded,
                await deadlineMutation.WaitAsync(timeout.Token));
            Assert.Equal(
                ChangeLegalTaskAssigneeResult.Succeeded,
                await taskMutation.WaitAsync(timeout.Token));

            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(
                graph.TargetMembership.Id,
                await GetDeadlineResponsibleAsync(
                    dbContext,
                    legalDeadline.Id,
                    timeout.Token));
            Assert.Equal(
                graph.TargetMembership.Id,
                await dbContext.LegalTasks
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == legalTask.Id)
                    .Select(candidate => candidate.AssigneeMembershipId)
                    .SingleAsync(timeout.Token));
            Assert.Equal(
                new[]
                {
                    AuditEventType.LegalTaskAssigneeChanged,
                    AuditEventType.LegalDeadlineResponsibleChanged
                }.OrderBy(eventType => eventType),
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            gate.ReleaseCreation();
            await DrainAsync(deadlineMutation);
            await DrainAsync(taskMutation);
        }
    }

    private async Task<ChangeLegalProcessResponsibleResult> ChangeResponsibleAsync(
        TestGraph graph,
        Guid processId,
        Guid? responsibleMembershipId,
        DbCommandInterceptor? interceptor,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        var useCase = new ChangeLegalProcessResponsibleUseCase(
            CreateProcessAuthorization(authorizationContext),
            CreateOperationalPersistence(interceptor));

        return await useCase.ExecuteAsync(
            new ChangeLegalProcessResponsibleCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                processId,
                responsibleMembershipId),
            cancellationToken);
    }

    private async Task<CreateLegalProcessResult> CreateProcessAsync(
        TestGraph graph,
        Guid clientId,
        Guid? responsibleMembershipId,
        DbCommandInterceptor? interceptor,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        DbContextOptionsBuilder<EnmaDbContext> builder =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString);

        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        var timeProvider = new FixedTimeProvider(Now.AddMinutes(3));
        var useCase = new CreateLegalProcessUseCase(
            CreateProcessAuthorization(authorizationContext),
            new ActiveClientInOrganizationLookup(authorizationContext),
            new LegalProcessCreationPersistence(builder.Options, timeProvider),
            timeProvider);

        return await useCase.ExecuteAsync(
            new CreateLegalProcessCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                clientId,
                "Created With Responsible",
                ResponsibleMembershipId: responsibleMembershipId),
            cancellationToken);
    }

    private async Task AssertCreationAndAssignmentPersistedAsync(
        TestGraph graph,
        Guid assignedProcessId,
        Guid createdProcessId,
        CancellationToken cancellationToken)
    {
        await AssertOpenProcessesHaveAvailableResponsibleAsync(
            graph,
            cancellationToken);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(
            graph.TargetMembership.Id,
            await GetResponsibleAsync(dbContext, assignedProcessId, cancellationToken));
        Assert.Equal(
            graph.TargetMembership.Id,
            await GetResponsibleAsync(dbContext, createdProcessId, cancellationToken));
        Assert.Equal(
            new[]
            {
                AuditEventType.LegalProcessCreated,
                AuditEventType.LegalProcessResponsibleChanged
            }.OrderBy(eventType => eventType),
            await FindAuditTypesAsync(dbContext, cancellationToken));
    }

    private async Task<Client> SeedClientAsync(TestGraph graph)
    {
        var client = new Client(
            graph.Organization.Id,
            "Creation Client",
            Now);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Add(client);
        await dbContext.SaveChangesAsync();

        return client;
    }

    private async Task<ChangeLegalProcessStatusResult> ChangeStatusAsync(
        TestGraph graph,
        Guid processId,
        string status,
        DbCommandInterceptor? interceptor,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        var useCase = new ChangeLegalProcessStatusUseCase(
            CreateProcessAuthorization(authorizationContext),
            CreateOperationalPersistence(interceptor));

        return await useCase.ExecuteAsync(
            new ChangeLegalProcessStatusCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                processId,
                status),
            cancellationToken);
    }

    private async Task<ChangeLegalProcessDetailsResult> ChangeDetailsAsync(
        TestGraph graph,
        Guid processId,
        DbCommandInterceptor? interceptor,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        var useCase = new ChangeLegalProcessDetailsUseCase(
            CreateProcessAuthorization(authorizationContext),
            CreateOperationalPersistence(interceptor));

        return await useCase.ExecuteAsync(
            new ChangeLegalProcessDetailsCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                processId,
                "Serialized Number",
                null),
            cancellationToken);
    }

    private Task<OrganizationMemberLifecycleMutationPersistenceResult>
        StartTargetDeactivation(
            TestGraph graph,
            DbCommandInterceptor interceptor,
            CancellationToken cancellationToken)
    {
        return CreateLifecyclePersistence(interceptor).ExecuteAsync(
            new OrganizationMemberLifecycleMutationPersistenceRequest(
                graph.ActorUser.Id,
                graph.Organization.Id,
                graph.ActorMembership.Id,
                graph.TargetMembership.Id,
                OrganizationMemberLifecycleOperation.Deactivate),
            cancellationToken);
    }

    private static ProcessActionAuthorization CreateProcessAuthorization(
        EnmaDbContext dbContext)
    {
        return new ProcessActionAuthorization(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(dbContext)));
    }

    private LegalProcessMutationPersistence CreateOperationalPersistence(
        DbCommandInterceptor? interceptor)
    {
        DbContextOptionsBuilder<EnmaDbContext> builder =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString);

        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new LegalProcessMutationPersistence(
            builder.Options,
            new FixedTimeProvider(Now.AddMinutes(2)));
    }

    private async Task AssertOpenProcessesHaveAvailableResponsibleAsync(
        TestGraph graph,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        int violations = await (
                from legalProcess in dbContext.LegalProcesses.AsNoTracking()
                join membership in dbContext.OrganizationMemberships.AsNoTracking()
                    on legalProcess.ResponsibleMembershipId equals (Guid?)membership.Id
                join user in dbContext.Users.AsNoTracking()
                    on membership.UserId equals user.Id
                where legalProcess.OrganizationId == graph.Organization.Id &&
                    legalProcess.Status != LegalProcessStatus.Closed &&
                    (!membership.IsActive || !user.IsActive)
                select legalProcess.Id)
            .CountAsync(cancellationToken);

        Assert.Equal(0, violations);
    }

    private static Task<Guid?> GetResponsibleAsync(
        EnmaDbContext dbContext,
        Guid processId,
        CancellationToken cancellationToken)
    {
        return dbContext.LegalProcesses
            .AsNoTracking()
            .Where(candidate => candidate.Id == processId)
            .Select(candidate => candidate.ResponsibleMembershipId)
            .SingleAsync(cancellationToken);
    }

    private async Task WaitForBlockedMembershipLockAsync(
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        while (true)
        {
            int count = await dbContext.Database.SqlQuery<int>(
                $"""
                SELECT COUNT(*)::integer AS "Value"
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock'
                  AND query ILIKE '%FROM organization_memberships%'
                  AND query ILIKE '%FOR UPDATE%'
                """).SingleAsync(cancellationToken);

            if (count > 0)
            {
                return;
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private Task<LegalProcessMutationPersistenceResult> StartPausedProcessMutation(
        TestGraph graph,
        LegalProcess legalProcess,
        ProcessLockGate gate,
        CancellationToken cancellationToken)
    {
        return CreateProcessMutationPersistence(
                new PauseAfterProcessLockInterceptor(gate))
            .UpdateTitleAsync(
                new LegalProcessMutationPersistenceRequest(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    graph.ActorMembership.Id,
                    legalProcess.Id),
                state =>
                {
                    if (!state.IsOrganizationActive ||
                        state.Actor?.IsAvailableFor(
                            graph.ActorUser.Id,
                            graph.Organization.Id,
                            graph.ActorMembership.Id) != true)
                    {
                        return LegalProcessMutationDecision.AccessDenied;
                    }

                    state.LegalProcess.ChangeTitle("Serialized Process");
                    return LegalProcessMutationDecision.Persist;
                },
                cancellationToken);
    }

    private Task<ClientCreationPersistenceResult> StartPausedClientCreation(
        TestGraph graph,
        string clientName,
        CrossSliceLockGate gate,
        CancellationToken cancellationToken)
    {
        return CreateClientPersistence(
                new PauseAfterMembershipLockInterceptor(gate))
            .ExecuteAsync(
                new ClientCreationPersistenceRequest(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    graph.ActorMembership.Id),
                state => state.IsOrganizationActive &&
                    state.Actor?.IsAvailableFor(
                        graph.ActorUser.Id,
                        graph.Organization.Id,
                        graph.ActorMembership.Id) == true
                        ? ClientCreationDecision.Persist(
                            new Client(
                                graph.Organization.Id,
                                clientName,
                                Now))
                        : ClientCreationDecision.AccessDenied,
                cancellationToken);
    }

    private ClientCreationPersistence CreateClientPersistence(
        DbCommandInterceptor interceptor)
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;

        return new ClientCreationPersistence(
            options,
            new FixedTimeProvider(Now));
    }

    private OrganizationNameMutationPersistence CreateRenamePersistence(
        DbCommandInterceptor interceptor)
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;

        return new OrganizationNameMutationPersistence(
            options,
            new FixedTimeProvider(Now.AddMinutes(1)));
    }

    private OrganizationMemberRoleMutationPersistence CreateRolePersistence(
        DbCommandInterceptor interceptor)
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;

        return new OrganizationMemberRoleMutationPersistence(
            options,
            new FixedTimeProvider(Now.AddMinutes(1)));
    }

    private OrganizationMemberLifecycleMutationPersistence
        CreateLifecyclePersistence(DbCommandInterceptor interceptor)
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;

        return new OrganizationMemberLifecycleMutationPersistence(
            options,
            new FixedTimeProvider(Now.AddMinutes(1)));
    }

    private LegalProcessMutationPersistence CreateProcessMutationPersistence(
        DbCommandInterceptor interceptor)
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;

        return new LegalProcessMutationPersistence(
            options,
            new FixedTimeProvider(Now.AddMinutes(1)));
    }

    private LegalTaskCreationPersistence CreateTaskPersistence()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;

        return new LegalTaskCreationPersistence(
            options,
            new FixedTimeProvider(Now));
    }

    private LegalTaskMutationPersistence CreateTaskMutationPersistence()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;

        return new LegalTaskMutationPersistence(
            options,
            new FixedTimeProvider(Now));
    }

    private async Task<TestGraph> SeedGraphAsync()
    {
        var organization = new Organization(
            "Cross Slice Legal",
            $"cross-slice-{Guid.NewGuid():N}",
            Now);
        var actorUser = new User(
            "Owner Actor",
            $"owner+{Guid.NewGuid():N}@example.test",
            Now);
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actorUser.Id,
            OrganizationRole.Owner,
            Now);
        var targetUser = new User(
            "Target Member",
            $"target+{Guid.NewGuid():N}@example.test",
            Now);
        var targetMembership = new OrganizationMembership(
            organization.Id,
            targetUser.Id,
            OrganizationRole.Member,
            Now);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(
            organization,
            actorUser,
            actorMembership,
            targetUser,
            targetMembership);
        await dbContext.SaveChangesAsync();

        return new TestGraph(
            organization,
            actorUser,
            actorMembership,
            targetMembership);
    }

    private async Task<LegalProcess> SeedProcessAsync(
        TestGraph graph,
        Guid? responsibleMembershipId = null,
        LegalProcessStatus status = LegalProcessStatus.InProgress)
    {
        var client = new Client(
            graph.Organization.Id,
            "Process Client",
            Now);
        var legalProcess = new LegalProcess(
            graph.Organization.Id,
            client.Id,
            "Original Process",
            Now);
        legalProcess.ChangeResponsible(responsibleMembershipId);
        legalProcess.ChangeStatus(status);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(client, legalProcess);
        await dbContext.SaveChangesAsync();

        return legalProcess;
    }

    private async Task<LegalTask> SeedLegalTaskAsync(TestGraph graph)
    {
        var legalTask = new LegalTask(
            graph.Organization.Id,
            "Original Task",
            null,
            null,
            null,
            null,
            graph.ActorMembership.Id,
            Now);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Add(legalTask);
        await dbContext.SaveChangesAsync();

        return legalTask;
    }

    private async Task WaitForBlockedProcessLockAsync(
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        while (true)
        {
            int count = await dbContext.Database.SqlQuery<int>(
                $"""
                SELECT COUNT(*)::integer AS "Value"
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock'
                  AND query ILIKE '%FROM legal_processes%'
                  AND query ILIKE '%FOR UPDATE%'
                """).SingleAsync(cancellationToken);

            if (count > 0)
            {
                return;
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static Task<List<AuditEventType>> FindAuditTypesAsync(
        EnmaDbContext dbContext,
        CancellationToken cancellationToken)
    {
        return dbContext.AuditLogs
            .AsNoTracking()
            .Select(auditLog => auditLog.EventType)
            .OrderBy(eventType => eventType)
            .ToListAsync(cancellationToken);
    }

    private static async Task DrainAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
        }
    }

    private async Task<ChangeLegalDeadlineResponsibleResult>
        ChangeDeadlineResponsibleAsync(
            TestGraph graph,
            Guid deadlineId,
            Guid? responsibleMembershipId,
            DbCommandInterceptor? interceptor,
            CancellationToken cancellationToken)
    {
        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        var useCase = new ChangeLegalDeadlineResponsibleUseCase(
            CreateDeadlineAuthorization(authorizationContext),
            CreateDeadlineMutationPersistence(interceptor));

        return await useCase.ExecuteAsync(
            new ChangeLegalDeadlineResponsibleCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                deadlineId,
                responsibleMembershipId),
            cancellationToken);
    }

    private async Task<ReopenLegalDeadlineResult> ReopenDeadlineAsync(
        TestGraph graph,
        Guid deadlineId,
        DbCommandInterceptor? interceptor,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        var useCase = new ReopenLegalDeadlineUseCase(
            CreateDeadlineAuthorization(authorizationContext),
            CreateDeadlineMutationPersistence(interceptor));

        return await useCase.ExecuteAsync(
            graph.ActorUser.Id,
            graph.Organization.Id,
            deadlineId,
            cancellationToken);
    }

    private async Task<CreateLegalDeadlineResult> CreateDeadlineAsync(
        TestGraph graph,
        Guid processId,
        Guid? responsibleMembershipId,
        DbCommandInterceptor? interceptor,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        var timeProvider = new FixedTimeProvider(Now.AddMinutes(3));
        var useCase = new CreateLegalDeadlineUseCase(
            CreateDeadlineAuthorization(authorizationContext),
            new ProcessOrganizationOwnershipLookup(authorizationContext),
            new LegalDeadlineCreationPersistence(
                CreateOptions(interceptor),
                timeProvider),
            timeProvider);

        return await useCase.ExecuteAsync(
            graph.ActorUser.Id,
            graph.Organization.Id,
            processId,
            "Created With Responsible",
            new DateOnly(2026, 9, 15),
            responsibleMembershipId,
            cancellationToken);
    }

    private async Task<ChangeLegalTaskAssigneeResult> ChangeTaskAssigneeAsync(
        TestGraph graph,
        Guid taskId,
        Guid? assigneeMembershipId,
        DbCommandInterceptor? interceptor,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        var useCase = new ChangeLegalTaskAssigneeUseCase(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(authorizationContext)),
            new LegalTaskMutationAuthorization(),
            new LegalTaskMutationPersistence(
                CreateOptions(interceptor),
                new FixedTimeProvider(Now.AddMinutes(2))));

        return await useCase.ExecuteAsync(
            new ChangeLegalTaskAssigneeCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                taskId,
                assigneeMembershipId),
            cancellationToken);
    }

    private static DeadlineActionAuthorization CreateDeadlineAuthorization(
        EnmaDbContext dbContext)
    {
        return new DeadlineActionAuthorization(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(dbContext)));
    }

    private LegalDeadlineMutationPersistence CreateDeadlineMutationPersistence(
        DbCommandInterceptor? interceptor)
    {
        return new LegalDeadlineMutationPersistence(
            CreateOptions(interceptor),
            new FixedTimeProvider(Now.AddMinutes(2)));
    }

    private DbContextOptions<EnmaDbContext> CreateOptions(
        DbCommandInterceptor? interceptor)
    {
        DbContextOptionsBuilder<EnmaDbContext> builder =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString);

        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return builder.Options;
    }

    private async Task<LegalDeadline> SeedDeadlineAsync(
        TestGraph graph,
        Guid? responsibleMembershipId = null,
        bool completed = false)
    {
        LegalProcess legalProcess = await SeedProcessAsync(graph);
        var legalDeadline = new LegalDeadline(
            graph.Organization.Id,
            legalProcess.Id,
            "Original Deadline",
            new DateOnly(2026, 9, 10),
            Now,
            responsibleMembershipId);

        if (completed)
        {
            legalDeadline.Complete(Now.AddMinutes(1));
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Add(legalDeadline);
        await dbContext.SaveChangesAsync();

        return legalDeadline;
    }

    private async Task AssertPendingDeadlinesHaveAvailableResponsibleAsync(
        TestGraph graph,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        int violations = await (
                from legalDeadline in dbContext.LegalDeadlines.AsNoTracking()
                join membership in dbContext.OrganizationMemberships.AsNoTracking()
                    on legalDeadline.ResponsibleMembershipId equals (Guid?)membership.Id
                join user in dbContext.Users.AsNoTracking()
                    on membership.UserId equals user.Id
                where legalDeadline.OrganizationId == graph.Organization.Id &&
                    legalDeadline.CompletedAt == null &&
                    (!membership.IsActive || !user.IsActive)
                select legalDeadline.Id)
            .CountAsync(cancellationToken);

        Assert.Equal(0, violations);
    }

    private static Task<Guid?> GetDeadlineResponsibleAsync(
        EnmaDbContext dbContext,
        Guid deadlineId,
        CancellationToken cancellationToken)
    {
        return dbContext.LegalDeadlines
            .AsNoTracking()
            .Where(candidate => candidate.Id == deadlineId)
            .Select(candidate => candidate.ResponsibleMembershipId)
            .SingleAsync(cancellationToken);
    }

    private sealed record TestGraph(
        Organization Organization,
        User ActorUser,
        OrganizationMembership ActorMembership,
        OrganizationMembership TargetMembership);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class CrossSliceLockGate
    {
        private readonly TaskCompletionSource<bool> _membershipLocked = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _organizationLocked = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseCreation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task MembershipLocked => _membershipLocked.Task;

        public Task OrganizationLocked => _organizationLocked.Task;

        public void SignalMembershipLocked() =>
            _membershipLocked.TrySetResult(true);

        public void SignalOrganizationLocked() =>
            _organizationLocked.TrySetResult(true);

        public Task WaitForCreationReleaseAsync(
            CancellationToken cancellationToken) =>
            _releaseCreation.Task.WaitAsync(cancellationToken);

        public void ReleaseCreation() =>
            _releaseCreation.TrySetResult(true);
    }

    private sealed class ProcessLockGate
    {
        private readonly TaskCompletionSource<bool> _processLocked = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseMutation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ProcessLocked => _processLocked.Task;

        public void SignalProcessLocked() =>
            _processLocked.TrySetResult(true);

        public Task WaitForMutationReleaseAsync(
            CancellationToken cancellationToken) =>
            _releaseMutation.Task.WaitAsync(cancellationToken);

        public void ReleaseMutation() =>
            _releaseMutation.TrySetResult(true);
    }

    private sealed class PauseAfterMembershipLockInterceptor(
        CrossSliceLockGate gate) : DbCommandInterceptor
    {
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "FROM organization_memberships",
                    StringComparison.Ordinal) &&
                command.CommandText.Contains(
                    "FOR UPDATE",
                    StringComparison.Ordinal))
            {
                gate.SignalMembershipLocked();
                await gate.WaitForCreationReleaseAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class SignalAfterOrganizationLockInterceptor(
        CrossSliceLockGate gate) : DbCommandInterceptor
    {
        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "FROM organizations",
                    StringComparison.Ordinal) &&
                command.CommandText.Contains(
                    "FOR UPDATE",
                    StringComparison.Ordinal))
            {
                gate.SignalOrganizationLocked();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class PauseAfterProcessLockInterceptor(
        ProcessLockGate gate) : DbCommandInterceptor
    {
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "FROM legal_processes",
                    StringComparison.Ordinal) &&
                command.CommandText.Contains(
                    "FOR UPDATE",
                    StringComparison.Ordinal))
            {
                gate.SignalProcessLocked();
                await gate.WaitForMutationReleaseAsync(cancellationToken);
            }

            return result;
        }
    }
}
