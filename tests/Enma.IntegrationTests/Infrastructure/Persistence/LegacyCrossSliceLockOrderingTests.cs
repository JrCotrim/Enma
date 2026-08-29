using System.Data.Common;
using Enma.Application.Clients;
using Enma.Application.Organizations.Members.Lifecycle;
using Enma.Application.Organizations.Members.Role;
using Enma.Application.Organizations.UpdateName;
using Enma.Application.Processes;
using Enma.Application.Tasks;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
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

    private async Task<LegalProcess> SeedProcessAsync(TestGraph graph)
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
