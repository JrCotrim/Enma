using System.Data;
using System.Data.Common;
using Enma.Application.Authorization;
using Enma.Application.Organizations.Members.Lifecycle;
using Enma.Application.Organizations.Members.Ownership;
using Enma.Application.Organizations.Members.Role;
using Enma.Application.Processes.Responsible;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationOwnershipTransferPersistenceConcurrencyTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        10,
        5,
        13,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TransferAndTargetDeactivation_InParallel_LeaveExactlyOneActiveOwner()
    {
        TestGraph graph = await SeedGraphAsync();
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blocker =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        await LockMembershipAsync(
            blockerContext,
            graph.Organization.Id,
            graph.TargetMembership.Id,
            timeout.Token);
        Task<OrganizationOwnershipTransferPersistenceResult>? transfer = null;
        Task<OrganizationMemberLifecycleMutationPersistenceResult>? deactivation = null;

        try
        {
            transfer = CreateTransferPersistence().ExecuteAsync(
                CreateTransferRequest(graph, graph.TargetMembership.Id),
                timeout.Token);
            deactivation = CreateLifecyclePersistence().ExecuteAsync(
                new OrganizationMemberLifecycleMutationPersistenceRequest(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    graph.ActorMembership.Id,
                    graph.TargetMembership.Id,
                    OrganizationMemberLifecycleOperation.Deactivate),
                timeout.Token);
            await WaitForBlockedMembershipLocksAsync(2, timeout.Token);

            await blocker.CommitAsync(timeout.Token);
            OrganizationOwnershipTransferPersistenceResult transferResult =
                await transfer.WaitAsync(timeout.Token);
            OrganizationMemberLifecycleMutationPersistenceResult deactivationResult =
                await deactivation.WaitAsync(timeout.Token);

            Guid owner = await FindSingleActiveOwnerAsync(graph.Organization.Id);
            if (transferResult == OrganizationOwnershipTransferPersistenceResult.Succeeded)
            {
                // The former Owner is now an Administrator and cannot
                // deactivate the new Owner.
                Assert.Equal(
                    OrganizationMemberLifecycleMutationPersistenceResult.AccessDenied,
                    deactivationResult);
                Assert.Equal(graph.TargetMembership.Id, owner);
                Assert.Equal(
                    [AuditEventType.OrganizationOwnershipTransferred],
                    await FindAuditTypesAsync());
            }
            else
            {
                Assert.Equal(
                    OrganizationOwnershipTransferPersistenceResult.TargetUnavailable,
                    transferResult);
                Assert.Equal(
                    OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
                    deactivationResult);
                Assert.Equal(graph.ActorMembership.Id, owner);
                Assert.Equal(
                    [AuditEventType.OrganizationMembershipDeactivated],
                    await FindAuditTypesAsync());
            }
        }
        finally
        {
            await RollbackIfActiveAsync(blocker);
            await DrainTaskAsync(transfer);
            await DrainTaskAsync(deactivation);
        }
    }

    [Fact]
    public async Task TargetDeactivatedWhileTransferWaits_RevalidatesAndReturnsTargetUnavailable()
    {
        TestGraph graph = await SeedGraphAsync();
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blocker =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        OrganizationMembership target = await LockMembershipAsync(
            blockerContext,
            graph.Organization.Id,
            graph.TargetMembership.Id,
            timeout.Token);
        target.Deactivate();
        await blockerContext.SaveChangesAsync(timeout.Token);
        Task<OrganizationOwnershipTransferPersistenceResult>? transfer = null;

        try
        {
            transfer = CreateTransferPersistence().ExecuteAsync(
                CreateTransferRequest(graph, graph.TargetMembership.Id),
                timeout.Token);
            await WaitForBlockedMembershipLocksAsync(1, timeout.Token);
            Assert.False(transfer.IsCompleted);

            await blocker.CommitAsync(timeout.Token);

            Assert.Equal(
                OrganizationOwnershipTransferPersistenceResult.TargetUnavailable,
                await transfer.WaitAsync(timeout.Token));
            Assert.Equal(
                graph.ActorMembership.Id,
                await FindSingleActiveOwnerAsync(graph.Organization.Id));
            Assert.Empty(await FindAuditTypesAsync());
        }
        finally
        {
            await RollbackIfActiveAsync(blocker);
            await DrainTaskAsync(transfer);
        }
    }

    [Fact]
    public async Task TransferAndTargetRoleChange_InParallel_LeaveExactlyOneActiveOwner()
    {
        TestGraph graph = await SeedGraphAsync();
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blocker =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        await LockMembershipAsync(
            blockerContext,
            graph.Organization.Id,
            graph.TargetMembership.Id,
            timeout.Token);
        Task<OrganizationOwnershipTransferPersistenceResult>? transfer = null;
        Task<OrganizationMemberRoleMutationPersistenceResult>? roleChange = null;

        try
        {
            transfer = CreateTransferPersistence().ExecuteAsync(
                CreateTransferRequest(graph, graph.TargetMembership.Id),
                timeout.Token);
            roleChange = CreateRolePersistence().ExecuteAsync(
                new OrganizationMemberRoleMutationPersistenceRequest(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    graph.ActorMembership.Id,
                    graph.TargetMembership.Id,
                    OrganizationRole.Member,
                    OrganizationRole.Administrator),
                timeout.Token);
            await WaitForBlockedMembershipLocksAsync(2, timeout.Token);

            await blocker.CommitAsync(timeout.Token);
            OrganizationOwnershipTransferPersistenceResult transferResult =
                await transfer.WaitAsync(timeout.Token);
            OrganizationMemberRoleMutationPersistenceResult roleResult =
                await roleChange.WaitAsync(timeout.Token);

            Guid owner = await FindSingleActiveOwnerAsync(graph.Organization.Id);
            if (transferResult == OrganizationOwnershipTransferPersistenceResult.Succeeded)
            {
                Assert.Equal(
                    OrganizationMemberRoleMutationPersistenceResult.AccessDenied,
                    roleResult);
                Assert.Equal(graph.TargetMembership.Id, owner);
                Assert.Equal(
                    OrganizationRole.Administrator,
                    await FindRoleAsync(graph.ActorMembership.Id));
                Assert.Equal(
                    [AuditEventType.OrganizationOwnershipTransferred],
                    await FindAuditTypesAsync());
            }
            else
            {
                Assert.Equal(
                    OrganizationOwnershipTransferPersistenceResult.TargetUnavailable,
                    transferResult);
                Assert.Equal(
                    OrganizationMemberRoleMutationPersistenceResult.Succeeded,
                    roleResult);
                Assert.Equal(graph.ActorMembership.Id, owner);
                Assert.Equal(
                    OrganizationRole.Member,
                    await FindRoleAsync(graph.TargetMembership.Id));
                Assert.Equal(
                    [AuditEventType.OrganizationMembershipRoleChanged],
                    await FindAuditTypesAsync());
            }
        }
        finally
        {
            await RollbackIfActiveAsync(blocker);
            await DrainTaskAsync(transfer);
            await DrainTaskAsync(roleChange);
        }
    }

    [Fact]
    public async Task TargetDemotedWhileTransferWaits_RevalidatesAndReturnsTargetUnavailable()
    {
        TestGraph graph = await SeedGraphAsync();
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blocker =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        OrganizationMembership target = await LockMembershipAsync(
            blockerContext,
            graph.Organization.Id,
            graph.TargetMembership.Id,
            timeout.Token);
        target.ChangeRole(OrganizationRole.Member);
        await blockerContext.SaveChangesAsync(timeout.Token);
        Task<OrganizationOwnershipTransferPersistenceResult>? transfer = null;

        try
        {
            transfer = CreateTransferPersistence().ExecuteAsync(
                CreateTransferRequest(graph, graph.TargetMembership.Id),
                timeout.Token);
            await WaitForBlockedMembershipLocksAsync(1, timeout.Token);

            await blocker.CommitAsync(timeout.Token);

            Assert.Equal(
                OrganizationOwnershipTransferPersistenceResult.TargetUnavailable,
                await transfer.WaitAsync(timeout.Token));
            Assert.Equal(
                graph.ActorMembership.Id,
                await FindSingleActiveOwnerAsync(graph.Organization.Id));
            Assert.Empty(await FindAuditTypesAsync());
        }
        finally
        {
            await RollbackIfActiveAsync(blocker);
            await DrainTaskAsync(transfer);
        }
    }

    [Fact]
    public async Task ActorDemotedWhileTransferWaits_DeniesWithoutWrite()
    {
        TestGraph graph = await SeedGraphAsync();
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blocker =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        OrganizationMembership actor = await LockMembershipAsync(
            blockerContext,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            timeout.Token);
        actor.ChangeRole(OrganizationRole.Administrator);
        await blockerContext.SaveChangesAsync(timeout.Token);
        Task<OrganizationOwnershipTransferPersistenceResult>? transfer = null;

        try
        {
            transfer = CreateTransferPersistence().ExecuteAsync(
                CreateTransferRequest(graph, graph.TargetMembership.Id),
                timeout.Token);
            await WaitForBlockedMembershipLocksAsync(1, timeout.Token);

            await blocker.CommitAsync(timeout.Token);

            Assert.Equal(
                OrganizationOwnershipTransferPersistenceResult.AccessDenied,
                await transfer.WaitAsync(timeout.Token));
            Assert.Equal(
                OrganizationRole.Administrator,
                await FindRoleAsync(graph.TargetMembership.Id));
            Assert.Empty(await FindAuditTypesAsync());
        }
        finally
        {
            await RollbackIfActiveAsync(blocker);
            await DrainTaskAsync(transfer);
        }
    }

    [Fact]
    public async Task DoubleTransferToDifferentAdministrators_InParallel_OnlyOneSucceeds()
    {
        TestGraph graph = await SeedGraphAsync();
        User secondUser = CreateUser("Second Administrator");
        secondUser.VerifyEmail(CreatedAt);
        var secondTarget = new OrganizationMembership(
            graph.Organization.Id,
            secondUser.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        await SeedAsync(secondUser, secondTarget);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blocker =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        await LockMembershipAsync(
            blockerContext,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            timeout.Token);
        OrganizationOwnershipTransferPersistence persistence =
            CreateTransferPersistence();
        Task<OrganizationOwnershipTransferPersistenceResult>? first = null;
        Task<OrganizationOwnershipTransferPersistenceResult>? second = null;

        try
        {
            first = persistence.ExecuteAsync(
                CreateTransferRequest(graph, graph.TargetMembership.Id),
                timeout.Token);
            second = persistence.ExecuteAsync(
                CreateTransferRequest(graph, secondTarget.Id),
                timeout.Token);
            await WaitForBlockedMembershipLocksAsync(2, timeout.Token);

            await blocker.CommitAsync(timeout.Token);
            OrganizationOwnershipTransferPersistenceResult[] results =
                await Task.WhenAll(first, second).WaitAsync(timeout.Token);

            Assert.Equal(
                1,
                results.Count(result =>
                    result == OrganizationOwnershipTransferPersistenceResult.Succeeded));
            Assert.Equal(
                1,
                results.Count(result =>
                    result == OrganizationOwnershipTransferPersistenceResult.AccessDenied));
            Guid owner = await FindSingleActiveOwnerAsync(graph.Organization.Id);
            Assert.Contains(owner, new[] { graph.TargetMembership.Id, secondTarget.Id });
            Assert.Equal(
                OrganizationRole.Administrator,
                await FindRoleAsync(graph.ActorMembership.Id));
            Assert.Equal(
                [AuditEventType.OrganizationOwnershipTransferred],
                await FindAuditTypesAsync());
        }
        finally
        {
            await RollbackIfActiveAsync(blocker);
            await DrainTaskAsync(first);
            await DrainTaskAsync(second);
        }
    }

    [Fact]
    public async Task IdenticalTransfers_InParallel_SecondIsDeniedAfterFirstCommits()
    {
        TestGraph graph = await SeedGraphAsync();
        using var timeout = CreateTimeout();
        OrganizationOwnershipTransferPersistence persistence =
            CreateTransferPersistence();
        OrganizationOwnershipTransferPersistenceRequest request =
            CreateTransferRequest(graph, graph.TargetMembership.Id);

        OrganizationOwnershipTransferPersistenceResult[] results =
            await Task.WhenAll(
                    persistence.ExecuteAsync(request, timeout.Token),
                    persistence.ExecuteAsync(request, timeout.Token))
                .WaitAsync(timeout.Token);

        Assert.Equal(
            [
                OrganizationOwnershipTransferPersistenceResult.AccessDenied,
                OrganizationOwnershipTransferPersistenceResult.Succeeded
            ],
            results.Order().ToArray());
        Assert.Equal(
            graph.TargetMembership.Id,
            await FindSingleActiveOwnerAsync(graph.Organization.Id));
        Assert.Equal(
            [AuditEventType.OrganizationOwnershipTransferred],
            await FindAuditTypesAsync());
    }

    [Fact]
    public async Task ProcessMutationHoldingMembershipLocksFirst_TransferRetriesWithoutDeadlock()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalProcess legalProcess = await SeedProcessAsync(graph);
        using var timeout = CreateTimeout();
        var gate = new MembershipLockGate();
        Task<ChangeLegalProcessResponsibleResult> processMutation =
            ChangeProcessResponsibleAsync(
                graph,
                legalProcess.Id,
                graph.TargetMembership.Id,
                new PauseAfterMembershipLockInterceptor(gate),
                timeout.Token);
        Task<OrganizationOwnershipTransferPersistenceResult>? transfer = null;

        try
        {
            await gate.MembershipLocked.WaitAsync(timeout.Token);
            transfer = CreateTransferPersistence().ExecuteAsync(
                CreateTransferRequest(graph, graph.TargetMembership.Id),
                timeout.Token);
            // The transfer's NOWAIT attempt fails and it waits outside its
            // organization lock, so the paused process can still lock it.
            await WaitForBlockedMembershipLocksAsync(1, timeout.Token);
            gate.Release();

            Assert.Equal(
                ChangeLegalProcessResponsibleResult.Succeeded,
                await processMutation.WaitAsync(timeout.Token));
            Assert.Equal(
                OrganizationOwnershipTransferPersistenceResult.Succeeded,
                await transfer.WaitAsync(timeout.Token));

            Assert.Equal(
                graph.TargetMembership.Id,
                await FindSingleActiveOwnerAsync(graph.Organization.Id));
            Assert.Equal(
                graph.TargetMembership.Id,
                await FindProcessResponsibleAsync(legalProcess.Id));
            Assert.Equal(
                [
                    AuditEventType.LegalProcessResponsibleChanged,
                    AuditEventType.OrganizationOwnershipTransferred
                ],
                await FindAuditTypesAsync());
        }
        finally
        {
            gate.Release();
            await DrainTaskAsync(processMutation);
            await DrainTaskAsync(transfer);
        }
    }

    [Fact]
    public async Task TransferHoldingLocksFirst_ProcessMutationOfTargetSerializesAfterIt()
    {
        TestGraph graph = await SeedGraphAsync();
        LegalProcess legalProcess = await SeedProcessAsync(graph);
        using var timeout = CreateTimeout();
        var gate = new MembershipLockGate();
        Task<OrganizationOwnershipTransferPersistenceResult> transfer =
            CreateTransferPersistence(new PauseAfterMembershipLockInterceptor(gate))
                .ExecuteAsync(
                    CreateTransferRequest(graph, graph.TargetMembership.Id),
                    timeout.Token);
        Task<ChangeLegalProcessResponsibleResult>? processMutation = null;

        try
        {
            await gate.MembershipLocked.WaitAsync(timeout.Token);
            processMutation = ChangeProcessResponsibleAsync(
                graph,
                legalProcess.Id,
                graph.TargetMembership.Id,
                interceptor: null,
                timeout.Token);
            await WaitForBlockedMembershipLocksAsync(1, timeout.Token);
            Assert.False(processMutation.IsCompleted);
            gate.Release();

            Assert.Equal(
                OrganizationOwnershipTransferPersistenceResult.Succeeded,
                await transfer.WaitAsync(timeout.Token));
            // The former Owner is now an Administrator, which may still
            // assign a process responsible.
            Assert.Equal(
                ChangeLegalProcessResponsibleResult.Succeeded,
                await processMutation.WaitAsync(timeout.Token));

            Assert.Equal(
                graph.TargetMembership.Id,
                await FindSingleActiveOwnerAsync(graph.Organization.Id));
            Assert.Equal(
                graph.TargetMembership.Id,
                await FindProcessResponsibleAsync(legalProcess.Id));
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            AuditLog processAudit = await dbContext.AuditLogs
                .AsNoTracking()
                .SingleAsync(auditLog =>
                    auditLog.EventType ==
                    AuditEventType.LegalProcessResponsibleChanged);
            Assert.Equal(
                OrganizationRole.Administrator,
                processAudit.ActorRoleAtOccurrence);
        }
        finally
        {
            gate.Release();
            await DrainTaskAsync(transfer);
            await DrainTaskAsync(processMutation);
        }
    }

    private OrganizationOwnershipTransferPersistence CreateTransferPersistence(
        DbCommandInterceptor? interceptor = null)
    {
        return new OrganizationOwnershipTransferPersistence(
            CreateOptions(interceptor),
            new FixedTimeProvider(CreatedAt.AddHours(1)));
    }

    private OrganizationMemberLifecycleMutationPersistence CreateLifecyclePersistence()
    {
        return new OrganizationMemberLifecycleMutationPersistence(
            CreateOptions(),
            new FixedTimeProvider(CreatedAt.AddHours(1)));
    }

    private OrganizationMemberRoleMutationPersistence CreateRolePersistence()
    {
        return new OrganizationMemberRoleMutationPersistence(
            CreateOptions(),
            new FixedTimeProvider(CreatedAt.AddHours(1)));
    }

    private DbContextOptions<EnmaDbContext> CreateOptions(
        DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString);

        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return builder.Options;
    }

    private async Task<ChangeLegalProcessResponsibleResult>
        ChangeProcessResponsibleAsync(
            TestGraph graph,
            Guid processId,
            Guid responsibleMembershipId,
            DbCommandInterceptor? interceptor,
            CancellationToken cancellationToken)
    {
        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        var useCase = new ChangeLegalProcessResponsibleUseCase(
            new ProcessActionAuthorization(
                new OrganizationAccessAuthorization(
                    new OrganizationAccessLookup(authorizationContext))),
            new LegalProcessMutationPersistence(
                CreateOptions(interceptor),
                new FixedTimeProvider(CreatedAt.AddHours(1))));

        return await useCase.ExecuteAsync(
            new ChangeLegalProcessResponsibleCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                processId,
                responsibleMembershipId),
            cancellationToken);
    }

    private static OrganizationOwnershipTransferPersistenceRequest CreateTransferRequest(
        TestGraph graph,
        Guid targetMembershipId)
    {
        return new OrganizationOwnershipTransferPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            targetMembershipId,
            OrganizationRole.Administrator);
    }

    private async Task<TestGraph> SeedGraphAsync()
    {
        var organization = new Organization(
            "Ownership Concurrency Legal",
            $"ownership-concurrency-{Guid.NewGuid():N}",
            CreatedAt);
        User actorUser = CreateUser("Owner Actor");
        User targetUser = CreateUser("Administrator Target");
        actorUser.VerifyEmail(CreatedAt);
        targetUser.VerifyEmail(CreatedAt);
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actorUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var targetMembership = new OrganizationMembership(
            organization.Id,
            targetUser.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        await SeedAsync(
            organization,
            actorUser,
            targetUser,
            actorMembership,
            targetMembership);

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
            "Ownership Process Client",
            CreatedAt);
        var legalProcess = new LegalProcess(
            graph.Organization.Id,
            client.Id,
            "Ownership Process",
            CreatedAt);
        await SeedAsync(client, legalProcess);
        return legalProcess;
    }

    private async Task WaitForBlockedMembershipLocksAsync(
        int minimumCount,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext observationContext = fixture.CreateDbContext();

        while (true)
        {
            int count = await observationContext.Database.SqlQuery<int>(
                $"""
                SELECT COUNT(*)::integer AS "Value"
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock'
                  AND query ILIKE '%FROM organization_memberships%'
                  AND query ILIKE '%FOR UPDATE%'
                """).SingleAsync(cancellationToken);

            if (count >= minimumCount)
            {
                return;
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static async Task<OrganizationMembership> LockMembershipAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid membershipId,
        CancellationToken cancellationToken)
    {
        return (await dbContext.OrganizationMemberships
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organization_memberships
                    WHERE organization_id = {organizationId}
                      AND id = {membershipId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .Single();
    }

    private async Task<Guid> FindSingleActiveOwnerAsync(Guid organizationId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        List<OrganizationMembership> owners = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership =>
                membership.OrganizationId == organizationId &&
                membership.Role == OrganizationRole.Owner)
            .ToListAsync();

        OrganizationMembership owner = Assert.Single(owners);
        Assert.True(owner.IsActive);
        return owner.Id;
    }

    private async Task<OrganizationRole> FindRoleAsync(Guid membershipId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership => membership.Id == membershipId)
            .Select(membership => membership.Role)
            .SingleAsync();
    }

    private async Task<Guid?> FindProcessResponsibleAsync(Guid processId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalProcesses
            .AsNoTracking()
            .Where(legalProcess => legalProcess.Id == processId)
            .Select(legalProcess => legalProcess.ResponsibleMembershipId)
            .SingleAsync();
    }

    private async Task<AuditEventType[]> FindAuditTypesAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuditLogs
            .AsNoTracking()
            .OrderBy(auditLog => auditLog.OccurredAt)
            .ThenBy(auditLog => auditLog.EventType)
            .Select(auditLog => auditLog.EventType)
            .ToArrayAsync();
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static Task<IDbContextTransaction> BeginTransactionAsync(
        EnmaDbContext dbContext,
        CancellationToken cancellationToken)
    {
        return dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
    }

    private static CancellationTokenSource CreateTimeout()
    {
        return new CancellationTokenSource(TimeSpan.FromSeconds(30));
    }

    private static async Task RollbackIfActiveAsync(
        IDbContextTransaction transaction)
    {
        if (transaction.GetDbTransaction().Connection is not null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static async Task DrainTaskAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static User CreateUser(string marker)
    {
        return new User(
            marker,
            $"{marker.ToLowerInvariant().Replace(' ', '.')}+{Guid.NewGuid():N}@example.test",
            CreatedAt);
    }

    private sealed record TestGraph(
        Organization Organization,
        User ActorUser,
        OrganizationMembership ActorMembership,
        OrganizationMembership TargetMembership);

    private sealed class MembershipLockGate
    {
        private readonly TaskCompletionSource<bool> _membershipLocked = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task MembershipLocked => _membershipLocked.Task;

        public void SignalMembershipLocked() =>
            _membershipLocked.TrySetResult(true);

        public Task WaitForReleaseAsync(CancellationToken cancellationToken) =>
            _release.Task.WaitAsync(cancellationToken);

        public void Release() => _release.TrySetResult(true);
    }

    // Pauses only the first membership lock of the intercepted workflow.
    private sealed class PauseAfterMembershipLockInterceptor(
        MembershipLockGate gate) : DbCommandInterceptor
    {
        private int _paused;

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
                    StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _paused, 1) == 0)
            {
                gate.SignalMembershipLocked();
                await gate.WaitForReleaseAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
