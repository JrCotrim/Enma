using System.Data;
using Enma.Application.Deadlines;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDeadlineMutationPersistenceConcurrencyTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private readonly Dictionary<Guid, (Guid UserId, Guid MembershipId)> _actors = [];

    private static readonly DateTimeOffset CreatedAt = new(
        2026, 8, 13, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly OriginalDueDate = new(2026, 9, 1);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task UpdateDetailsAsync_CompetingSameDeadlineUpdate_SerializesAndLastReplacementWins()
    {
        (Organization organization, _, _, LegalDeadline deadline) =
            await SeedDeadlineAsync();
        using var timeout = CreateTimeout();
        await using EnmaDbContext firstContext = fixture.CreateDbContext();
        await using IDbContextTransaction firstTransaction =
            await BeginTransactionAsync(firstContext, timeout.Token);
        LegalDeadline firstDeadline = await LockDeadlineAsync(
            firstContext,
            deadline.Id,
            organization.Id,
            timeout.Token);
        firstDeadline.ChangeDetails("First update", new DateOnly(2026, 10, 1));
        await firstContext.SaveChangesAsync(timeout.Token);

        Task<LegalDeadlineDetailsMutationPersistenceResult>? secondMutation = null;

        try
        {
            secondMutation = UpdateDetailsAsync(
                CreatePersistence(),
                deadline.Id,
                organization.Id,
                "Second update",
                new DateOnly(2026, 11, 1),
                timeout.Token);
            await WaitForBlockedDeadlineLockAsync(timeout.Token);
            Assert.False(secondMutation.IsCompleted);

            await firstTransaction.CommitAsync(timeout.Token);
            LegalDeadlineDetailsMutationPersistenceResult secondResult =
                await secondMutation.WaitAsync(timeout.Token);

            Assert.Equal(
                LegalDeadlineDetailsMutationPersistenceResult.Updated,
                secondResult);
            LegalDeadline persisted = await FindDeadlineAsync(
                deadline.Id,
                timeout.Token);
            Assert.Equal("Second update", persisted.Title);
            Assert.Equal(new DateOnly(2026, 11, 1), persisted.DueDate);
            Assert.Null(persisted.CompletedAt);
        }
        finally
        {
            await RollbackIfActiveAsync(firstTransaction);
            await DrainTaskAsync(secondMutation);
        }
    }

    [Fact]
    public async Task CompleteAsync_WhenUpdateLocksFirst_WaitsAndPreservesUpdatedDetails()
    {
        (Organization organization, _, _, LegalDeadline deadline) =
            await SeedDeadlineAsync();
        DateTimeOffset completionTime = CreatedAt.AddHours(2);
        using var timeout = CreateTimeout();
        await using EnmaDbContext updateContext = fixture.CreateDbContext();
        await using IDbContextTransaction updateTransaction =
            await BeginTransactionAsync(updateContext, timeout.Token);
        LegalDeadline updatingDeadline = await LockDeadlineAsync(
            updateContext,
            deadline.Id,
            organization.Id,
            timeout.Token);
        updatingDeadline.ChangeDetails(
            "Updated before complete",
            new DateOnly(2026, 10, 15));
        await updateContext.SaveChangesAsync(timeout.Token);

        Task<LegalDeadlineLifecycleMutationPersistenceResult>? completion = null;

        try
        {
            completion = CompleteAsync(
                CreatePersistence(),
                deadline.Id,
                organization.Id,
                completionTime,
                timeout.Token);
            await WaitForBlockedDeadlineLockAsync(timeout.Token);
            Assert.False(completion.IsCompleted);

            await updateTransaction.CommitAsync(timeout.Token);
            Assert.Equal(
                LegalDeadlineLifecycleMutationPersistenceResult.Succeeded,
                await completion.WaitAsync(timeout.Token));

            LegalDeadline persisted = await FindDeadlineAsync(
                deadline.Id,
                timeout.Token);
            Assert.Equal("Updated before complete", persisted.Title);
            Assert.Equal(new DateOnly(2026, 10, 15), persisted.DueDate);
            Assert.Equal(completionTime, persisted.CompletedAt);
        }
        finally
        {
            await RollbackIfActiveAsync(updateTransaction);
            await DrainTaskAsync(completion);
        }
    }

    [Fact]
    public async Task UpdateDetailsAsync_WhenCompleteLocksFirst_WaitsThenReturnsConflict()
    {
        (Organization organization, _, _, LegalDeadline deadline) =
            await SeedDeadlineAsync();
        DateTimeOffset completionTime = CreatedAt.AddHours(2);
        using var timeout = CreateTimeout();
        await using EnmaDbContext completionContext = fixture.CreateDbContext();
        await using IDbContextTransaction completionTransaction =
            await BeginTransactionAsync(completionContext, timeout.Token);
        LegalDeadline completingDeadline = await LockDeadlineAsync(
            completionContext,
            deadline.Id,
            organization.Id,
            timeout.Token);
        completingDeadline.Complete(completionTime);
        await completionContext.SaveChangesAsync(timeout.Token);

        Task<LegalDeadlineDetailsMutationPersistenceResult>? update = null;

        try
        {
            update = UpdateDetailsAsync(
                CreatePersistence(),
                deadline.Id,
                organization.Id,
                "Forbidden update",
                new DateOnly(2026, 10, 15),
                timeout.Token);
            await WaitForBlockedDeadlineLockAsync(timeout.Token);
            Assert.False(update.IsCompleted);

            await completionTransaction.CommitAsync(timeout.Token);
            Assert.Equal(
                LegalDeadlineDetailsMutationPersistenceResult.Conflict,
                await update.WaitAsync(timeout.Token));

            LegalDeadline persisted = await FindDeadlineAsync(
                deadline.Id,
                timeout.Token);
            Assert.Equal("Initial title", persisted.Title);
            Assert.Equal(OriginalDueDate, persisted.DueDate);
            Assert.Equal(completionTime, persisted.CompletedAt);
        }
        finally
        {
            await RollbackIfActiveAsync(completionTransaction);
            await DrainTaskAsync(update);
        }
    }

    [Fact]
    public async Task CompleteAsync_CompetingCompletion_PreservesFirstSerializedTimestamp()
    {
        (Organization organization, _, _, LegalDeadline deadline) =
            await SeedDeadlineAsync();
        DateTimeOffset firstCompletionTime = CreatedAt.AddHours(1);
        DateTimeOffset secondCompletionTime = CreatedAt.AddHours(2);
        using var timeout = CreateTimeout();
        await using EnmaDbContext firstContext = fixture.CreateDbContext();
        await using IDbContextTransaction firstTransaction =
            await BeginTransactionAsync(firstContext, timeout.Token);
        LegalDeadline firstDeadline = await LockDeadlineAsync(
            firstContext,
            deadline.Id,
            organization.Id,
            timeout.Token);
        firstDeadline.Complete(firstCompletionTime);
        await firstContext.SaveChangesAsync(timeout.Token);

        Task<LegalDeadlineLifecycleMutationPersistenceResult>? secondCompletion = null;

        try
        {
            secondCompletion = CompleteAsync(
                CreatePersistence(),
                deadline.Id,
                organization.Id,
                secondCompletionTime,
                timeout.Token);
            await WaitForBlockedDeadlineLockAsync(timeout.Token);
            Assert.False(secondCompletion.IsCompleted);

            await firstTransaction.CommitAsync(timeout.Token);
            Assert.Equal(
                LegalDeadlineLifecycleMutationPersistenceResult.Succeeded,
                await secondCompletion.WaitAsync(timeout.Token));
            Assert.Equal(
                firstCompletionTime,
                (await FindDeadlineAsync(deadline.Id, timeout.Token)).CompletedAt);
        }
        finally
        {
            await RollbackIfActiveAsync(firstTransaction);
            await DrainTaskAsync(secondCompletion);
        }
    }

    [Fact]
    public async Task ReopenAsync_WhenCompleteLocksFirst_WaitsAndFinalStateIsPending()
    {
        (Organization organization, _, _, LegalDeadline deadline) =
            await SeedDeadlineAsync();
        using var timeout = CreateTimeout();
        await using EnmaDbContext completionContext = fixture.CreateDbContext();
        await using IDbContextTransaction completionTransaction =
            await BeginTransactionAsync(completionContext, timeout.Token);
        LegalDeadline completingDeadline = await LockDeadlineAsync(
            completionContext,
            deadline.Id,
            organization.Id,
            timeout.Token);
        completingDeadline.Complete(CreatedAt.AddHours(1));
        await completionContext.SaveChangesAsync(timeout.Token);

        Task<LegalDeadlineLifecycleMutationPersistenceResult>? reopen = null;

        try
        {
            reopen = ReopenAsync(
                CreatePersistence(),
                deadline.Id,
                organization.Id,
                timeout.Token);
            await WaitForBlockedDeadlineLockAsync(timeout.Token);
            Assert.False(reopen.IsCompleted);

            await completionTransaction.CommitAsync(timeout.Token);
            Assert.Equal(
                LegalDeadlineLifecycleMutationPersistenceResult.Succeeded,
                await reopen.WaitAsync(timeout.Token));
            Assert.Null(
                (await FindDeadlineAsync(deadline.Id, timeout.Token)).CompletedAt);
        }
        finally
        {
            await RollbackIfActiveAsync(completionTransaction);
            await DrainTaskAsync(reopen);
        }
    }

    [Fact]
    public async Task CompleteAsync_WhenReopenLocksFirst_WaitsAndFinalStateIsCompleted()
    {
        (Organization organization, _, _, LegalDeadline deadline) =
            await SeedDeadlineAsync(completedAt: CreatedAt.AddHours(1));
        DateTimeOffset recompletionTime = CreatedAt.AddHours(2);
        using var timeout = CreateTimeout();
        await using EnmaDbContext reopenContext = fixture.CreateDbContext();
        await using IDbContextTransaction reopenTransaction =
            await BeginTransactionAsync(reopenContext, timeout.Token);
        LegalDeadline reopeningDeadline = await LockDeadlineAsync(
            reopenContext,
            deadline.Id,
            organization.Id,
            timeout.Token);
        reopeningDeadline.Reopen();
        await reopenContext.SaveChangesAsync(timeout.Token);

        Task<LegalDeadlineLifecycleMutationPersistenceResult>? completion = null;

        try
        {
            completion = CompleteAsync(
                CreatePersistence(),
                deadline.Id,
                organization.Id,
                recompletionTime,
                timeout.Token);
            await WaitForBlockedDeadlineLockAsync(timeout.Token);
            Assert.False(completion.IsCompleted);

            await reopenTransaction.CommitAsync(timeout.Token);
            Assert.Equal(
                LegalDeadlineLifecycleMutationPersistenceResult.Succeeded,
                await completion.WaitAsync(timeout.Token));
            Assert.Equal(
                recompletionTime,
                (await FindDeadlineAsync(deadline.Id, timeout.Token)).CompletedAt);
        }
        finally
        {
            await RollbackIfActiveAsync(reopenTransaction);
            await DrainTaskAsync(completion);
        }
    }

    [Fact]
    public async Task UpdateDetailsAsync_WhenReopenLocksFirst_WaitsThenUpdatesPendingDeadline()
    {
        (Organization organization, _, _, LegalDeadline deadline) =
            await SeedDeadlineAsync(completedAt: CreatedAt.AddHours(1));
        using var timeout = CreateTimeout();
        await using EnmaDbContext reopenContext = fixture.CreateDbContext();
        await using IDbContextTransaction reopenTransaction =
            await BeginTransactionAsync(reopenContext, timeout.Token);
        LegalDeadline reopeningDeadline = await LockDeadlineAsync(
            reopenContext,
            deadline.Id,
            organization.Id,
            timeout.Token);
        reopeningDeadline.Reopen();
        await reopenContext.SaveChangesAsync(timeout.Token);

        Task<LegalDeadlineDetailsMutationPersistenceResult>? update = null;

        try
        {
            update = UpdateDetailsAsync(
                CreatePersistence(),
                deadline.Id,
                organization.Id,
                "Updated after reopen",
                new DateOnly(2026, 12, 1),
                timeout.Token);
            await WaitForBlockedDeadlineLockAsync(timeout.Token);
            Assert.False(update.IsCompleted);

            await reopenTransaction.CommitAsync(timeout.Token);
            Assert.Equal(
                LegalDeadlineDetailsMutationPersistenceResult.Updated,
                await update.WaitAsync(timeout.Token));

            LegalDeadline persisted = await FindDeadlineAsync(
                deadline.Id,
                timeout.Token);
            Assert.Equal("Updated after reopen", persisted.Title);
            Assert.Equal(new DateOnly(2026, 12, 1), persisted.DueDate);
            Assert.Null(persisted.CompletedAt);
        }
        finally
        {
            await RollbackIfActiveAsync(reopenTransaction);
            await DrainTaskAsync(update);
        }
    }

    [Fact]
    public async Task CompleteAsync_WithDifferentDeadline_DoesNotUseGlobalDeadlineLock()
    {
        (Organization organization, Client client, LegalProcess process,
            LegalDeadline deadlineA) = await SeedDeadlineAsync();
        var deadlineB = new LegalDeadline(
            organization.Id,
            process.Id,
            "Deadline B",
            new DateOnly(2026, 10, 1),
            CreatedAt);
        await SeedAsync(deadlineB);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        await LockDeadlineAsync(
            blockerContext,
            deadlineA.Id,
            organization.Id,
            timeout.Token);

        try
        {
            LegalDeadlineLifecycleMutationPersistenceResult result =
                await CompleteAsync(
                    CreatePersistence(),
                    deadlineB.Id,
                    organization.Id,
                    CreatedAt.AddHours(1),
                    timeout.Token)
                .WaitAsync(timeout.Token);

            Assert.Equal(
                LegalDeadlineLifecycleMutationPersistenceResult.Succeeded,
                result);
            LegalDeadline persistedB = await FindDeadlineAsync(
                deadlineB.Id,
                timeout.Token);
            Assert.Equal(CreatedAt.AddHours(1), persistedB.CompletedAt);
            Assert.Equal(process.Id, persistedB.ProcessId);
            Assert.Equal(organization.Id, client.OrganizationId);
        }
        finally
        {
            await RollbackIfActiveAsync(blockerTransaction);
        }
    }

    [Fact]
    public async Task ReopenAsync_WithWrongTenantWhileRowLocked_ReturnsNotFoundWithoutAcquiringRow()
    {
        Organization organizationA = CreateOrganization(
            "Wrong Context Organization",
            "wrong-deadline-context-organization");
        Organization organizationB = CreateOrganization(
            "Owning Organization",
            "owning-deadline-organization");
        var clientB = new Client(organizationB.Id, "Owning Client", CreatedAt);
        var processB = new LegalProcess(
            organizationB.Id,
            clientB.Id,
            "Owning Process",
            CreatedAt);
        var deadlineB = new LegalDeadline(
            organizationB.Id,
            processB.Id,
            "Protected deadline",
            OriginalDueDate,
            CreatedAt);
        deadlineB.Complete(CreatedAt.AddHours(1));
        await SeedAsync(organizationA, organizationB, clientB, processB, deadlineB);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        await LockDeadlineAsync(
            blockerContext,
            deadlineB.Id,
            organizationB.Id,
            timeout.Token);

        try
        {
            LegalDeadlineLifecycleMutationPersistenceResult result =
                await ReopenAsync(
                    CreatePersistence(),
                    deadlineB.Id,
                    organizationA.Id,
                    timeout.Token)
                .WaitAsync(timeout.Token);

            Assert.Equal(
                LegalDeadlineLifecycleMutationPersistenceResult.NotFound,
                result);
        }
        finally
        {
            await RollbackIfActiveAsync(blockerTransaction);
        }

        LegalDeadline persisted = await FindDeadlineAsync(deadlineB.Id);
        Assert.Equal(CreatedAt.AddHours(1), persisted.CompletedAt);
        Assert.Equal("Protected deadline", persisted.Title);
        Assert.Equal(organizationB.Id, persisted.OrganizationId);
        Assert.Equal(processB.Id, persisted.ProcessId);
    }

    [Fact]
    public async Task CompleteAsync_CompetingTransitions_EmitExactlyOneAudit()
    {
        (Organization organization, _, _, LegalDeadline deadline) =
            await SeedDeadlineAsync();
        DateTimeOffset firstTimestamp = CreatedAt.AddHours(1);
        DateTimeOffset secondTimestamp = CreatedAt.AddHours(2);
        using var timeout = CreateTimeout();

        LegalDeadlineLifecycleMutationPersistenceResult[] results =
            await Task.WhenAll(
                CompleteAsync(
                    CreatePersistence(),
                    deadline.Id,
                    organization.Id,
                    firstTimestamp,
                    timeout.Token),
                CompleteAsync(
                    CreatePersistence(),
                    deadline.Id,
                    organization.Id,
                    secondTimestamp,
                    timeout.Token));

        Assert.All(results, result => Assert.Equal(
            LegalDeadlineLifecycleMutationPersistenceResult.Succeeded,
            result));
        LegalDeadline persisted = await FindDeadlineAsync(
            deadline.Id,
            timeout.Token);
        Assert.Contains(
            Assert.IsType<DateTimeOffset>(persisted.CompletedAt),
            new[] { firstTimestamp, secondTimestamp });
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        AuditLog auditLog = await verificationContext.AuditLogs
            .AsNoTracking()
            .SingleAsync(timeout.Token);
        Assert.Equal(AuditEventType.LegalDeadlineCompleted, auditLog.EventType);
        Assert.Equal(deadline.Id, auditLog.EntityId);
        Assert.Null(auditLog.Details);
    }

    private LegalDeadlineMutationPersistence CreatePersistence()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;
        return new LegalDeadlineMutationPersistence(options, TimeProvider.System);
    }

    private Task<LegalDeadlineDetailsMutationPersistenceResult> UpdateDetailsAsync(
        LegalDeadlineMutationPersistence persistence,
        Guid deadlineId,
        Guid organizationId,
        string title,
        DateOnly dueDate,
        CancellationToken cancellationToken)
    {
        return persistence.UpdateDetailsAsync(
            CreateRequest(organizationId, deadlineId),
            state =>
            {
                if (state.LegalDeadline.CompletedAt is not null)
                {
                    return LegalDeadlineMutationDecision.Conflict;
                }

                state.LegalDeadline.ChangeDetails(title, dueDate);
                return LegalDeadlineMutationDecision.Persist;
            },
            cancellationToken);
    }

    private Task<LegalDeadlineLifecycleMutationPersistenceResult> CompleteAsync(
        LegalDeadlineMutationPersistence persistence,
        Guid deadlineId,
        Guid organizationId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        return persistence.CompleteAsync(
            CreateRequest(organizationId, deadlineId),
            state =>
            {
                state.LegalDeadline.Complete(completedAt);
                return LegalDeadlineMutationDecision.Persist;
            },
            cancellationToken);
    }

    private Task<LegalDeadlineLifecycleMutationPersistenceResult> ReopenAsync(
        LegalDeadlineMutationPersistence persistence,
        Guid deadlineId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return persistence.ReopenAsync(
            CreateRequest(organizationId, deadlineId),
            state =>
            {
                state.LegalDeadline.Reopen();
                return LegalDeadlineMutationDecision.Persist;
            },
            cancellationToken);
    }

    private LegalDeadlineMutationPersistenceRequest CreateRequest(
        Guid organizationId,
        Guid deadlineId)
    {
        (Guid userId, Guid membershipId) = _actors[organizationId];
        return new LegalDeadlineMutationPersistenceRequest(
            userId,
            organizationId,
            membershipId,
            deadlineId);
    }

    private async Task WaitForBlockedDeadlineLockAsync(
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
                      AND query ILIKE '%FROM legal_deadlines%'
                      AND query ILIKE '%FOR UPDATE%'
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

    private static async Task<LegalDeadline> LockDeadlineAsync(
        EnmaDbContext dbContext,
        Guid deadlineId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        LegalDeadline[] deadlines = await dbContext.LegalDeadlines
            .FromSqlInterpolated(
                $"""
                SELECT * FROM legal_deadlines
                WHERE id = {deadlineId}
                  AND organization_id = {organizationId}
                FOR UPDATE
                """)
            .ToArrayAsync(cancellationToken);
        return Assert.Single(deadlines);
    }

    private static Task<IDbContextTransaction> BeginTransactionAsync(
        EnmaDbContext dbContext,
        CancellationToken cancellationToken)
    {
        return dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
    }

    private async Task<(Organization, Client, LegalProcess, LegalDeadline)>
        SeedDeadlineAsync(DateTimeOffset? completedAt = null)
    {
        Organization organization = CreateOrganization(
            "Concurrent Deadline Organization",
            "concurrent-deadline-organization");
        var client = new Client(organization.Id, "Concurrent Client", CreatedAt);
        var process = new LegalProcess(
            organization.Id,
            client.Id,
            "Concurrent Process",
            CreatedAt);
        var deadline = new LegalDeadline(
            organization.Id,
            process.Id,
            "Initial title",
            OriginalDueDate,
            CreatedAt);

        if (completedAt.HasValue)
        {
            deadline.Complete(completedAt.Value);
        }

        await SeedAsync(organization, client, process, deadline);
        return (organization, client, process, deadline);
    }

    private async Task<LegalDeadline> FindDeadlineAsync(
        Guid deadlineId,
        CancellationToken cancellationToken = default)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalDeadlines
            .AsNoTracking()
            .SingleAsync(
                candidate => candidate.Id == deadlineId,
                cancellationToken);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        foreach (Organization organization in entities.OfType<Organization>())
        {
            var user = new User(
                "Concurrent deadline actor",
                $"deadline-concurrency-{organization.Id:N}@example.test",
                CreatedAt);
            var membership = new OrganizationMembership(
                organization.Id,
                user.Id,
                OrganizationRole.Owner,
                CreatedAt);
            dbContext.AddRange(user, membership);
            _actors[organization.Id] = (user.Id, membership.Id);
        }

        await dbContext.SaveChangesAsync();
    }

    private static Organization CreateOrganization(string name, string slug)
    {
        return new Organization(name, slug, CreatedAt);
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
}
