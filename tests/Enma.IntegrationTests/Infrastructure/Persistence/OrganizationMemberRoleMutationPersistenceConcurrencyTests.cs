using System.Data;
using Enma.Application.Organizations.Members.Role;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationMemberRoleMutationPersistenceConcurrencyTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        25,
        13,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ExecuteAsync_TargetChangesWhileWaiting_UsesPostLockRole()
    {
        TestGraph graph = await SeedGraphAsync();
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        OrganizationMembership target = await LockMembershipAsync(
            blockerContext,
            graph.Organization.Id,
            graph.TargetMembership.Id,
            timeout.Token);
        target.ChangeRole(OrganizationRole.Administrator);
        await blockerContext.SaveChangesAsync(timeout.Token);
        Task<OrganizationMemberRoleMutationPersistenceResult>? mutation = null;

        try
        {
            mutation = CreatePersistence().ExecuteAsync(
                CreateRequest(
                    graph,
                    OrganizationRole.Member,
                    OrganizationRole.Administrator),
                timeout.Token);
            await WaitForBlockedMembershipLockAsync(timeout.Token);
            Assert.False(mutation.IsCompleted);

            await blockerTransaction.CommitAsync(timeout.Token);
            OrganizationMemberRoleMutationPersistenceResult result =
                await mutation.WaitAsync(timeout.Token);

            Assert.Equal(
                OrganizationMemberRoleMutationPersistenceResult.Succeeded,
                result);
            Assert.Equal(
                OrganizationRole.Member,
                await FindRoleAsync(graph.TargetMembership.Id));
        }
        finally
        {
            await RollbackIfActiveAsync(blockerTransaction);
            await DrainTaskAsync(mutation);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TargetChangesToUnexpectedRole_ReturnsConflict()
    {
        TestGraph graph = await SeedGraphAsync();
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        OrganizationMembership target = await LockMembershipAsync(
            blockerContext,
            graph.Organization.Id,
            graph.TargetMembership.Id,
            timeout.Token);
        target.ChangeRole(OrganizationRole.Administrator);
        await blockerContext.SaveChangesAsync(timeout.Token);
        Task<OrganizationMemberRoleMutationPersistenceResult>? mutation = null;

        try
        {
            mutation = CreatePersistence().ExecuteAsync(
                CreateRequest(
                    graph,
                    OrganizationRole.Member,
                    OrganizationRole.Member),
                timeout.Token);
            await WaitForBlockedMembershipLockAsync(timeout.Token);

            await blockerTransaction.CommitAsync(timeout.Token);
            OrganizationMemberRoleMutationPersistenceResult result =
                await mutation.WaitAsync(timeout.Token);

            Assert.Equal(
                OrganizationMemberRoleMutationPersistenceResult.Conflict,
                result);
            Assert.Equal(
                OrganizationRole.Administrator,
                await FindRoleAsync(graph.TargetMembership.Id));
        }
        finally
        {
            await RollbackIfActiveAsync(blockerTransaction);
            await DrainTaskAsync(mutation);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ActorDemotedWhileWaiting_DeniesWithoutWrite()
    {
        TestGraph graph = await SeedGraphAsync();
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        OrganizationMembership actor = await LockMembershipAsync(
            blockerContext,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            timeout.Token);
        actor.ChangeRole(OrganizationRole.Administrator);
        await blockerContext.SaveChangesAsync(timeout.Token);
        Task<OrganizationMemberRoleMutationPersistenceResult>? mutation = null;

        try
        {
            mutation = CreatePersistence().ExecuteAsync(
                CreateRequest(
                    graph,
                    OrganizationRole.Administrator,
                    OrganizationRole.Member),
                timeout.Token);
            await WaitForBlockedMembershipLockAsync(timeout.Token);

            await blockerTransaction.CommitAsync(timeout.Token);
            OrganizationMemberRoleMutationPersistenceResult result =
                await mutation.WaitAsync(timeout.Token);

            Assert.Equal(
                OrganizationMemberRoleMutationPersistenceResult.AccessDenied,
                result);
            Assert.Equal(
                OrganizationRole.Member,
                await FindRoleAsync(graph.TargetMembership.Id));
        }
        finally
        {
            await RollbackIfActiveAsync(blockerTransaction);
            await DrainTaskAsync(mutation);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrentIdenticalChanges_SerializeWithoutLostUpdate()
    {
        TestGraph graph = await SeedGraphAsync();
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginTransactionAsync(blockerContext, timeout.Token);
        await LockMembershipAsync(
            blockerContext,
            graph.Organization.Id,
            graph.TargetMembership.Id,
            timeout.Token);
        OrganizationMemberRoleMutationPersistence persistence = CreatePersistence();
        OrganizationMemberRoleMutationPersistenceRequest request = CreateRequest(
            graph,
            OrganizationRole.Administrator,
            OrganizationRole.Member);
        Task<OrganizationMemberRoleMutationPersistenceResult>? first = null;
        Task<OrganizationMemberRoleMutationPersistenceResult>? second = null;

        try
        {
            first = persistence.ExecuteAsync(request, timeout.Token);
            await WaitForBlockedMembershipLockAsync(timeout.Token);
            second = persistence.ExecuteAsync(request, timeout.Token);
            await WaitForBlockedLockAsync("organizations", timeout.Token);

            await blockerTransaction.CommitAsync(timeout.Token);
            OrganizationMemberRoleMutationPersistenceResult[] results =
                await Task.WhenAll(first, second).WaitAsync(timeout.Token);

            Assert.All(
                results,
                result => Assert.Equal(
                    OrganizationMemberRoleMutationPersistenceResult.Succeeded,
                    result));
            Assert.Equal(
                OrganizationRole.Administrator,
                await FindRoleAsync(graph.TargetMembership.Id));
        }
        finally
        {
            await RollbackIfActiveAsync(blockerTransaction);
            await DrainTaskAsync(first);
            await DrainTaskAsync(second);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ActorAndTargetLockSetsUseSameOrder_NoDeadlock()
    {
        Organization organization = CreateOrganization();
        User firstUser = CreateUser("First Owner");
        User secondUser = CreateUser("Second Owner");
        var firstMembership = new OrganizationMembership(
            organization.Id,
            firstUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var secondMembership = new OrganizationMembership(
            organization.Id,
            secondUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        await SeedAsync(
            organization,
            firstUser,
            secondUser,
            firstMembership,
            secondMembership);
        using var timeout = CreateTimeout();
        OrganizationMemberRoleMutationPersistence persistence = CreatePersistence();
        var firstRequest = new OrganizationMemberRoleMutationPersistenceRequest(
            firstUser.Id,
            organization.Id,
            firstMembership.Id,
            secondMembership.Id,
            OrganizationRole.Member,
            OrganizationRole.Administrator);
        var secondRequest = new OrganizationMemberRoleMutationPersistenceRequest(
            secondUser.Id,
            organization.Id,
            secondMembership.Id,
            firstMembership.Id,
            OrganizationRole.Member,
            OrganizationRole.Administrator);

        OrganizationMemberRoleMutationPersistenceResult[] results =
            await Task.WhenAll(
                    persistence.ExecuteAsync(firstRequest, timeout.Token),
                    persistence.ExecuteAsync(secondRequest, timeout.Token))
                .WaitAsync(timeout.Token);

        Assert.All(
            results,
            result => Assert.Equal(
                OrganizationMemberRoleMutationPersistenceResult.TargetForbidden,
                result));
        Assert.Equal(OrganizationRole.Owner, await FindRoleAsync(firstMembership.Id));
        Assert.Equal(OrganizationRole.Owner, await FindRoleAsync(secondMembership.Id));
    }

    private OrganizationMemberRoleMutationPersistence CreatePersistence()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;
        return new OrganizationMemberRoleMutationPersistence(options);
    }

    private static OrganizationMemberRoleMutationPersistenceRequest CreateRequest(
        TestGraph graph,
        OrganizationRole role,
        OrganizationRole expectedCurrentRole)
    {
        return new OrganizationMemberRoleMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.TargetMembership.Id,
            role,
            expectedCurrentRole);
    }

    private async Task<TestGraph> SeedGraphAsync()
    {
        Organization organization = CreateOrganization();
        User actorUser = CreateUser("Owner Actor");
        User targetUser = CreateUser("Member Target");
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actorUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var targetMembership = new OrganizationMembership(
            organization.Id,
            targetUser.Id,
            OrganizationRole.Member,
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

    private async Task WaitForBlockedMembershipLockAsync(
        CancellationToken cancellationToken)
    {
        await WaitForBlockedLockAsync(
            "organization_memberships",
            cancellationToken);
    }

    private async Task WaitForBlockedLockAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext observationContext = fixture.CreateDbContext();
        string tablePattern = $"%FROM {tableName}%";

        while (true)
        {
            int count = await observationContext.Database.SqlQuery<int>(
                $"""
                SELECT COUNT(*)::integer AS "Value"
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock'
                  AND query ILIKE {tablePattern}
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

    private async Task<OrganizationRole> FindRoleAsync(Guid membershipId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership => membership.Id == membershipId)
            .Select(membership => membership.Role)
            .SingleAsync();
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

    private static Organization CreateOrganization()
    {
        return new Organization(
            "Concurrency Legal",
            $"concurrency-{Guid.NewGuid():N}",
            CreatedAt);
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
}
