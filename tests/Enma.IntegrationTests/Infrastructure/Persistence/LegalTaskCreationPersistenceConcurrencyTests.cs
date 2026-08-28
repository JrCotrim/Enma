using System.Data;
using Enma.Application.Authorization;
using Enma.Application.Tasks;
using Enma.Application.Tasks.Create;
using Enma.Domain.Organizations;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalTaskCreationPersistenceConcurrencyTests(
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

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateAsync_WhenActorDemotionCommitsFirst_UsesLiveMemberRoleAndDeniesOtherAssignment()
    {
        TenantMembers graph = await SeedTenantMembersAsync(
            OrganizationRole.Administrator);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        OrganizationMembership actorMembership = await blockerContext
            .OrganizationMemberships
            .SingleAsync(
                membership => membership.Id == graph.ActorMembership.Id,
                timeout.Token);
        actorMembership.ChangeRole(OrganizationRole.Member);
        await blockerContext.SaveChangesAsync(timeout.Token);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        CreateLegalTaskUseCase useCase = CreateUseCase(queryContext);
        Task<CreateLegalTaskResult>? creation = null;

        try
        {
            creation = useCase.ExecuteAsync(
                CreateCommand(graph, graph.TargetMembership.Id),
                timeout.Token);
            await WaitForBlockedCreateLockAsync(
                "organization_memberships",
                timeout.Token);
            Assert.False(creation.IsCompleted);

            await blockerTransaction.CommitAsync(timeout.Token);
            CreateLegalTaskResult result = await creation.WaitAsync(timeout.Token);

            Assert.Same(CreateLegalTaskResult.AccessDenied, result);
            Assert.Equal(0, await CountTasksAsync(timeout.Token));
        }
        finally
        {
            await RollbackIfActiveAsync(blockerTransaction);
            await DrainTaskAsync(creation);
        }
    }

    [Fact]
    public async Task CreateAsync_WhenActorMembershipDeactivationCommitsFirst_RejectsCreation()
    {
        TenantMembers graph = await SeedTenantMembersAsync(OrganizationRole.Owner);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        OrganizationMembership actorMembership = await blockerContext
            .OrganizationMemberships
            .SingleAsync(
                membership => membership.Id == graph.ActorMembership.Id,
                timeout.Token);
        actorMembership.Deactivate();
        await blockerContext.SaveChangesAsync(timeout.Token);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        CreateLegalTaskUseCase useCase = CreateUseCase(queryContext);
        Task<CreateLegalTaskResult>? creation = null;

        try
        {
            creation = useCase.ExecuteAsync(
                CreateCommand(graph, assigneeMembershipId: null),
                timeout.Token);
            await WaitForBlockedCreateLockAsync(
                "organization_memberships",
                timeout.Token);
            Assert.False(creation.IsCompleted);

            await blockerTransaction.CommitAsync(timeout.Token);
            CreateLegalTaskResult result = await creation.WaitAsync(timeout.Token);

            Assert.Same(CreateLegalTaskResult.AccessDenied, result);
            Assert.Equal(0, await CountTasksAsync(timeout.Token));
        }
        finally
        {
            await RollbackIfActiveAsync(blockerTransaction);
            await DrainTaskAsync(creation);
        }
    }

    [Fact]
    public async Task CreateAsync_WhenTargetMembershipDeactivationCommitsFirst_ReturnsUnavailable()
    {
        TenantMembers graph = await SeedTenantMembersAsync(OrganizationRole.Owner);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        OrganizationMembership targetMembership = await blockerContext
            .OrganizationMemberships
            .SingleAsync(
                membership => membership.Id == graph.TargetMembership.Id,
                timeout.Token);
        targetMembership.Deactivate();
        await blockerContext.SaveChangesAsync(timeout.Token);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        CreateLegalTaskUseCase useCase = CreateUseCase(queryContext);
        Task<CreateLegalTaskResult>? creation = null;

        try
        {
            creation = useCase.ExecuteAsync(
                CreateCommand(graph, graph.TargetMembership.Id),
                timeout.Token);
            await WaitForBlockedCreateLockAsync(
                "organization_memberships",
                timeout.Token);
            Assert.False(creation.IsCompleted);

            await blockerTransaction.CommitAsync(timeout.Token);
            CreateLegalTaskResult result = await creation.WaitAsync(timeout.Token);

            Assert.Same(CreateLegalTaskResult.RelatedAssigneeUnavailable, result);
            Assert.Equal(0, await CountTasksAsync(timeout.Token));
        }
        finally
        {
            await RollbackIfActiveAsync(blockerTransaction);
            await DrainTaskAsync(creation);
        }
    }

    [Fact]
    public async Task CreateAsync_WhenTargetUserDeactivationCommitsFirst_ReturnsUnavailable()
    {
        TenantMembers graph = await SeedTenantMembersAsync(OrganizationRole.Owner);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        User targetUser = await blockerContext.Users.SingleAsync(
            user => user.Id == graph.TargetUser.Id,
            timeout.Token);
        targetUser.Deactivate();
        await blockerContext.SaveChangesAsync(timeout.Token);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        CreateLegalTaskUseCase useCase = CreateUseCase(queryContext);
        Task<CreateLegalTaskResult>? creation = null;

        try
        {
            creation = useCase.ExecuteAsync(
                CreateCommand(graph, graph.TargetMembership.Id),
                timeout.Token);
            await WaitForBlockedCreateLockAsync("users", timeout.Token);
            Assert.False(creation.IsCompleted);

            await blockerTransaction.CommitAsync(timeout.Token);
            CreateLegalTaskResult result = await creation.WaitAsync(timeout.Token);

            Assert.Same(CreateLegalTaskResult.RelatedAssigneeUnavailable, result);
            Assert.Equal(0, await CountTasksAsync(timeout.Token));
        }
        finally
        {
            await RollbackIfActiveAsync(blockerTransaction);
            await DrainTaskAsync(creation);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenCreateLocksFirst_AssignmentCommitsBeforeTargetDeactivation()
    {
        TenantMembers graph = await SeedTenantMembersAsync(OrganizationRole.Owner);
        using var timeout = CreateTimeout();
        using var releaseDecision = new ManualResetEventSlim(false);
        var locksAcquired = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        LegalTaskCreationPersistence persistence = CreatePersistence();
        var request = new LegalTaskCreationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.TargetMembership.Id);
        Task<LegalTaskCreationPersistenceResult>? creation = null;
        Task? deactivation = null;

        try
        {
            creation = Task.Run(
                () => persistence.ExecuteAsync(
                    request,
                    lockedState =>
                    {
                        Assert.True(lockedState.Actor?.IsMembershipActive);
                        Assert.True(lockedState.Assignee?.IsMembershipActive);
                        locksAcquired.TrySetResult(true);
                        releaseDecision.Wait(timeout.Token);

                        return LegalTaskCreationDecision.Persist(
                            new LegalTask(
                                graph.Organization.Id,
                                "Create wins",
                                null,
                                null,
                                null,
                                graph.TargetMembership.Id,
                                graph.ActorMembership.Id,
                                TaskCreatedAt));
                    },
                    timeout.Token),
                timeout.Token);
            await locksAcquired.Task.WaitAsync(timeout.Token);
            deactivation = DeactivateMembershipAsync(
                graph.TargetMembership.Id,
                timeout.Token);
            await WaitForBlockedUpdateAsync(
                "organization_memberships",
                timeout.Token);
            Assert.False(deactivation.IsCompleted);

            releaseDecision.Set();
            LegalTaskCreationPersistenceResult creationResult =
                await creation.WaitAsync(timeout.Token);
            await deactivation.WaitAsync(timeout.Token);

            Assert.Equal(
                LegalTaskCreationDecisionStatus.Persist,
                creationResult.Status);
            LegalTask persisted = await FindTaskAsync(
                Assert.IsType<Guid>(creationResult.LegalTaskId),
                timeout.Token);
            Assert.Equal(
                graph.TargetMembership.Id,
                persisted.AssigneeMembershipId);
            Assert.False(await IsMembershipActiveAsync(
                graph.TargetMembership.Id,
                timeout.Token));
        }
        finally
        {
            releaseDecision.Set();
            await DrainTaskAsync(creation);
            await DrainTaskAsync(deactivation);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UnrelatedOrganizationLock_DoesNotBlockCreation()
    {
        TenantMembers graphA = await SeedTenantMembersAsync(
            OrganizationRole.Owner,
            "Organization A",
            "organization-a",
            "actor-a@example.test",
            "target-a@example.test");
        TenantMembers graphB = await SeedTenantMembersAsync(
            OrganizationRole.Owner,
            "Organization B",
            "organization-b",
            "actor-b@example.test",
            "target-b@example.test");
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        OrganizationMembership membershipB = await blockerContext
            .OrganizationMemberships
            .SingleAsync(
                membership => membership.Id == graphB.ActorMembership.Id,
                timeout.Token);
        membershipB.Deactivate();
        await blockerContext.SaveChangesAsync(timeout.Token);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        CreateLegalTaskUseCase useCase = CreateUseCase(queryContext);

        try
        {
            CreateLegalTaskResult result = await useCase.ExecuteAsync(
                CreateCommand(graphA, graphA.TargetMembership.Id),
                timeout.Token);

            Assert.Equal(CreateLegalTaskResultStatus.Succeeded, result.Status);
        }
        finally
        {
            await blockerTransaction.RollbackAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_SaveFailure_RollsBackWithoutPartialTask()
    {
        TenantMembers graph = await SeedTenantMembersAsync(OrganizationRole.Owner);
        LegalTaskCreationPersistence persistence = CreatePersistence();
        var request = new LegalTaskCreationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            null);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            persistence.ExecuteAsync(
                request,
                _ => LegalTaskCreationDecision.Persist(
                    new LegalTask(
                        graph.Organization.Id,
                        "Invalid related process",
                        null,
                        null,
                        Guid.NewGuid(),
                        null,
                        graph.ActorMembership.Id,
                        TaskCreatedAt))));

        Assert.Equal(0, await CountTasksAsync());
    }

    private CreateLegalTaskUseCase CreateUseCase(EnmaDbContext queryContext)
    {
        return new CreateLegalTaskUseCase(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(queryContext)),
            new ProcessOrganizationOwnershipLookup(queryContext),
            CreatePersistence(),
            new FixedTimeProvider(TaskCreatedAt));
    }

    private LegalTaskCreationPersistence CreatePersistence()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;
        return new LegalTaskCreationPersistence(
            options,
            new FixedTimeProvider(TaskCreatedAt));
    }

    private async Task WaitForBlockedCreateLockAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        string tablePattern = $"%FROM {tableName}%";
        await WaitForBlockedQueryAsync(
            tablePattern,
            "%FOR UPDATE%",
            cancellationToken);
    }

    private async Task WaitForBlockedUpdateAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        string updatePattern = $"%UPDATE {tableName}%";
        await WaitForBlockedQueryAsync(
            updatePattern,
            updatePattern,
            cancellationToken);
    }

    private async Task WaitForBlockedQueryAsync(
        string firstPattern,
        string secondPattern,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext observationContext = fixture.CreateDbContext();

        while (true)
        {
            int waitingCommandCount = await observationContext.Database
                .SqlQuery<int>(
                    $"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND wait_event_type = 'Lock'
                      AND query ILIKE {firstPattern}
                      AND query ILIKE {secondPattern}
                    """)
                .SingleAsync(cancellationToken);

            if (waitingCommandCount > 0)
            {
                return;
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task DeactivateMembershipAsync(
        Guid membershipId,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembership membership = await dbContext
            .OrganizationMemberships
            .SingleAsync(
                candidate => candidate.Id == membershipId,
                cancellationToken);
        membership.Deactivate();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> IsMembershipActiveAsync(
        Guid membershipId,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.OrganizationMemberships
            .Where(membership => membership.Id == membershipId)
            .Select(membership => membership.IsActive)
            .SingleAsync(cancellationToken);
    }

    private async Task<TenantMembers> SeedTenantMembersAsync(
        OrganizationRole actorRole,
        string organizationName = "Concurrent Organization",
        string organizationSlug = "concurrent-organization",
        string actorEmail = "concurrent-actor@example.test",
        string targetEmail = "concurrent-target@example.test")
    {
        var organization = new Organization(
            organizationName,
            organizationSlug,
            SeededAt);
        var actorUser = new User("Actor", actorEmail, SeededAt);
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actorUser.Id,
            actorRole,
            SeededAt);
        var targetUser = new User("Target", targetEmail, SeededAt);
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

        return new TenantMembers(
            organization,
            actorUser,
            actorMembership,
            targetUser,
            targetMembership);
    }

    private async Task<LegalTask> FindTaskAsync(
        Guid legalTaskId,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalTasks
            .AsNoTracking()
            .SingleAsync(
                legalTask => legalTask.Id == legalTaskId,
                cancellationToken);
    }

    private async Task<int> CountTasksAsync(
        CancellationToken cancellationToken = default)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalTasks.CountAsync(cancellationToken);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static CreateLegalTaskCommand CreateCommand(
        TenantMembers graph,
        Guid? assigneeMembershipId)
    {
        return new CreateLegalTaskCommand(
            graph.ActorUser.Id,
            graph.Organization.Id,
            "Concurrent create",
            null,
            null,
            null,
            assigneeMembershipId);
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
        return new CancellationTokenSource(TimeSpan.FromSeconds(20));
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
}
