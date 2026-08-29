using System.Data;
using Enma.Application.Clients;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class ClientMutationPersistenceConcurrencyTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private readonly Dictionary<Guid, (Guid UserId, Guid MembershipId)> _actors = [];

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        12,
        17,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task UpdateNameAsync_CompetingSameTenantMutation_SerializesAtClientRowLock()
    {
        Organization organization = CreateOrganization(
            "Concurrent Organization",
            "concurrent-organization");
        var client = new Client(organization.Id, "Initial", CreatedAt);
        await SeedAsync(organization, client);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using EnmaDbContext firstContext = fixture.CreateDbContext();
        await using IDbContextTransaction firstTransaction =
            await firstContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        Client firstClient = await LockClientAsync(
            firstContext,
            client.Id,
            organization.Id,
            timeout.Token);
        firstClient.ChangeName("Alpha");
        await firstContext.SaveChangesAsync(timeout.Token);

        Task<ClientMutationPersistenceResult>? secondMutation = null;

        try
        {
            secondMutation = UpdateNameAsync(
                CreatePersistence(),
                client.Id,
                organization.Id,
                "Beta",
                timeout.Token);

            await WaitForBlockedClientLockAsync(timeout.Token);

            Assert.False(secondMutation.IsCompleted);

            await firstTransaction.CommitAsync(timeout.Token);

            ClientMutationPersistenceResult secondResult =
                await secondMutation.WaitAsync(timeout.Token);

            Assert.Equal(
                ClientMutationPersistenceResult.Succeeded,
                secondResult);

            await using EnmaDbContext verificationContext = fixture.CreateDbContext();
            Client persistedClient = await verificationContext.Clients
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == client.Id, timeout.Token);

            Assert.Equal("Beta", persistedClient.Name);
            Assert.Equal(organization.Id, persistedClient.OrganizationId);
        }
        finally
        {
            if (firstTransaction.GetDbTransaction().Connection is not null)
            {
                await firstTransaction.RollbackAsync(CancellationToken.None);
            }

            await DrainTaskAsync(secondMutation);
        }
    }

    [Fact]
    public async Task UpdateNameAsync_WithDifferentTenantClient_DoesNotUseGlobalLock()
    {
        Organization organizationA = CreateOrganization(
            "Concurrent Organization A",
            "concurrent-organization-a");
        Organization organizationB = CreateOrganization(
            "Concurrent Organization B",
            "concurrent-organization-b");
        var clientA = new Client(organizationA.Id, "Client A", CreatedAt);
        var clientB = new Client(organizationB.Id, "Client B", CreatedAt);
        await SeedAsync(organizationA, organizationB, clientA, clientB);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await LockClientAsync(
            blockerContext,
            clientA.Id,
            organizationA.Id,
            timeout.Token);

        try
        {
            ClientMutationPersistenceResult result = await UpdateNameAsync(
                    CreatePersistence(),
                    clientB.Id,
                    organizationB.Id,
                    "Updated Client B",
                    timeout.Token)
                .WaitAsync(timeout.Token);

            Assert.Equal(ClientMutationPersistenceResult.Succeeded, result);

            await using EnmaDbContext verificationContext = fixture.CreateDbContext();
            Client persistedClientB = await verificationContext.Clients
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == clientB.Id, timeout.Token);

            Assert.Equal("Updated Client B", persistedClientB.Name);
            Assert.Equal(organizationB.Id, persistedClientB.OrganizationId);
        }
        finally
        {
            if (blockerTransaction.GetDbTransaction().Connection is not null)
            {
                await blockerTransaction.RollbackAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task DeactivateAsync_CompetingTransitions_EmitExactlyOneAudit()
    {
        Organization organization = CreateOrganization(
            "Concurrent transition organization",
            "concurrent-transition-organization");
        var client = new Client(organization.Id, "Transition client", CreatedAt);
        await SeedAsync(organization, client);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        ClientMutationPersistenceResult[] results = await Task.WhenAll(
            DeactivateAsync(
                CreatePersistence(),
                client.Id,
                organization.Id,
                timeout.Token),
            DeactivateAsync(
                CreatePersistence(),
                client.Id,
                organization.Id,
                timeout.Token));

        Assert.All(results, result => Assert.Equal(
            ClientMutationPersistenceResult.Succeeded,
            result));
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.False((await verificationContext.Clients
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == client.Id, timeout.Token))
            .IsActive);
        AuditLog auditLog = await verificationContext.AuditLogs
            .AsNoTracking()
            .SingleAsync(timeout.Token);
        Assert.Equal(AuditEventType.ClientDeactivated, auditLog.EventType);
        Assert.Equal(client.Id, auditLog.EntityId);
        Assert.Null(auditLog.Details);
    }

    private ClientMutationPersistence CreatePersistence()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;

        return new ClientMutationPersistence(options, TimeProvider.System);
    }

    private Task<ClientMutationPersistenceResult> UpdateNameAsync(
        ClientMutationPersistence persistence,
        Guid clientId,
        Guid organizationId,
        string name,
        CancellationToken cancellationToken)
    {
        (Guid userId, Guid membershipId) = _actors[organizationId];
        return persistence.UpdateNameAsync(
            new ClientMutationPersistenceRequest(
                userId,
                organizationId,
                membershipId,
                clientId),
            state =>
            {
                state.Client.ChangeName(name);
                return ClientMutationDecision.Persist;
            },
            cancellationToken);
    }

    private Task<ClientMutationPersistenceResult> DeactivateAsync(
        ClientMutationPersistence persistence,
        Guid clientId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        (Guid userId, Guid membershipId) = _actors[organizationId];
        return persistence.DeactivateAsync(
            new ClientMutationPersistenceRequest(
                userId,
                organizationId,
                membershipId,
                clientId),
            state =>
            {
                state.Client.Deactivate();
                return ClientMutationDecision.Persist;
            },
            cancellationToken);
    }

    private async Task WaitForBlockedClientLockAsync(
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
                      AND query ILIKE '%FROM clients%'
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

    private static async Task<Client> LockClientAsync(
        EnmaDbContext dbContext,
        Guid clientId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        Client[] clients = await dbContext.Clients
            .FromSqlInterpolated(
                $"""
                SELECT * FROM clients
                WHERE id = {clientId}
                  AND organization_id = {organizationId}
                FOR UPDATE
                """)
            .ToArrayAsync(cancellationToken);

        return Assert.Single(clients);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        foreach (Organization organization in entities.OfType<Organization>())
        {
            var user = new User(
                "Client audit actor",
                $"client-{organization.Id:N}@example.test",
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
