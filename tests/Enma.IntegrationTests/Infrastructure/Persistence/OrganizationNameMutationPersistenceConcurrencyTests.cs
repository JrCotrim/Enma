using System.Data;
using System.Data.Common;
using Enma.Application.Organizations.UpdateName;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationNameMutationPersistenceConcurrencyTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        26,
        13,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ExecuteAsync_ActorDemotedWhileRenameWaits_Denies()
    {
        TestGraph graph = await SeedGraphAsync();
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        await LockOrganizationAsync(
            blockerContext,
            graph.Organization.Id,
            timeout.Token);
        OrganizationMembership actor = await LockMembershipAsync(
            blockerContext,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            timeout.Token);
        actor.ChangeRole(OrganizationRole.Administrator);
        await blockerContext.SaveChangesAsync(timeout.Token);
        Task<OrganizationNameMutationPersistenceResult>? mutation = null;

        try
        {
            mutation = CreatePersistence().ExecuteAsync(
                CreateRequest(graph, "Denied Legal"),
                timeout.Token);
            await WaitForBlockedOrganizationLockAsync(timeout.Token);
            Assert.False(mutation.IsCompleted);

            await blockerTransaction.CommitAsync(timeout.Token);
            OrganizationNameMutationPersistenceResult result =
                await mutation.WaitAsync(timeout.Token);

            Assert.Equal(
                OrganizationNameMutationPersistenceResult.AccessDenied,
                result);
            Assert.Equal(
                graph.Organization.Name,
                await FindNameAsync(graph.Organization.Id));
            Assert.Equal(0, await CountAuditLogsAsync());
        }
        finally
        {
            await RollbackIfActiveAsync(blockerTransaction);
            await DrainTaskAsync(mutation);
        }
    }

    [Fact]
    public async Task ExecuteAsync_OrganizationDeactivatedWhileRenameWaits_Denies()
    {
        TestGraph graph = await SeedGraphAsync();
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        Organization organization = await LockOrganizationAsync(
            blockerContext,
            graph.Organization.Id,
            timeout.Token);
        organization.Deactivate();
        await blockerContext.SaveChangesAsync(timeout.Token);
        Task<OrganizationNameMutationPersistenceResult>? mutation = null;

        try
        {
            mutation = CreatePersistence().ExecuteAsync(
                CreateRequest(graph, "Denied Legal"),
                timeout.Token);
            await WaitForBlockedOrganizationLockAsync(timeout.Token);
            Assert.False(mutation.IsCompleted);

            await blockerTransaction.CommitAsync(timeout.Token);
            OrganizationNameMutationPersistenceResult result =
                await mutation.WaitAsync(timeout.Token);

            Assert.Equal(
                OrganizationNameMutationPersistenceResult.AccessDenied,
                result);
            Assert.Equal(
                graph.Organization.Name,
                await FindNameAsync(graph.Organization.Id));
            Assert.Equal(0, await CountAuditLogsAsync());
        }
        finally
        {
            await RollbackIfActiveAsync(blockerTransaction);
            await DrainTaskAsync(mutation);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrentAuthorizedRenames_SerializeLastCommittedWins()
    {
        TestGraph graph = await SeedGraphAsync();
        using var timeout = CreateTimeout();
        var pause = new PauseAfterOrganizationLockInterceptor();
        Task<OrganizationNameMutationPersistenceResult>? first = null;
        Task<OrganizationNameMutationPersistenceResult>? second = null;

        try
        {
            first = CreatePersistence(pause).ExecuteAsync(
                CreateRequest(graph, "First Legal"),
                timeout.Token);
            await pause.LockAcquired.WaitAsync(timeout.Token);

            second = CreatePersistence().ExecuteAsync(
                CreateRequest(graph, "Second Legal"),
                timeout.Token);
            await WaitForBlockedOrganizationLockAsync(timeout.Token);
            Assert.False(second.IsCompleted);

            pause.Release();
            OrganizationNameMutationPersistenceResult[] results =
                await Task.WhenAll(first, second).WaitAsync(timeout.Token);

            Assert.All(
                results,
                result => Assert.Equal(
                    OrganizationNameMutationPersistenceResult.Succeeded,
                    result));
            Assert.Equal(
                "Second Legal",
                await FindNameAsync(graph.Organization.Id));
            Assert.Equal(2, await CountAuditLogsAsync());
        }
        finally
        {
            pause.Release();
            await DrainTaskAsync(first);
            await DrainTaskAsync(second);
        }
    }

    private OrganizationNameMutationPersistence CreatePersistence(
        DbCommandInterceptor? interceptor = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString);

        if (interceptor is not null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        return new OrganizationNameMutationPersistence(
            optionsBuilder.Options,
            new FixedTimeProvider(CreatedAt.AddHours(1)));
    }

    private static OrganizationNameMutationPersistenceRequest CreateRequest(
        TestGraph graph,
        string name)
    {
        return new OrganizationNameMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            name);
    }

    private async Task<TestGraph> SeedGraphAsync()
    {
        var organization = new Organization(
            "Concurrency Legal",
            $"concurrency-{Guid.NewGuid():N}",
            CreatedAt);
        var actorUser = new User(
            "Owner Actor",
            $"owner+{Guid.NewGuid():N}@example.test",
            CreatedAt);
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actorUser.Id,
            OrganizationRole.Owner,
            CreatedAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(organization, actorUser, actorMembership);
        await dbContext.SaveChangesAsync();

        return new TestGraph(organization, actorUser, actorMembership);
    }

    private async Task WaitForBlockedOrganizationLockAsync(
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
                  AND query ILIKE '%FROM organizations%'
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

    private static async Task<Organization> LockOrganizationAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return (await dbContext.Organizations
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organizations
                    WHERE id = {organizationId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .Single();
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

    private async Task<string> FindNameAsync(Guid organizationId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(organization => organization.Name)
            .SingleAsync();
    }

    private async Task<int> CountAuditLogsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuditLogs.CountAsync();
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

    private sealed record TestGraph(
        Organization Organization,
        User ActorUser,
        OrganizationMembership ActorMembership);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class PauseAfterOrganizationLockInterceptor
        : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> _lockAcquired = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task LockAcquired => _lockAcquired.Task;

        public void Release() => _release.TrySetResult(true);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
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
                _lockAcquired.TrySetResult(true);
                await _release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
