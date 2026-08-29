using System.Data;
using Enma.Application.Processes;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalProcessMutationPersistenceConcurrencyTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private readonly Dictionary<Guid, (Guid UserId, Guid MembershipId)> _actors = [];

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        13,
        20,
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
    public async Task UpdateTitleAsync_CompetingSameProcessMutation_SerializesAtProcessRowLock()
    {
        Organization organization = CreateOrganization(
            "Concurrent Organization",
            "concurrent-process-organization");
        var client = new Client(organization.Id, "Concurrent Client", CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            "Initial title",
            CreatedAt);
        await SeedAsync(organization, client, legalProcess);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using EnmaDbContext firstContext = fixture.CreateDbContext();
        await using IDbContextTransaction firstTransaction =
            await firstContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        LegalProcess firstProcess = await LockLegalProcessAsync(
            firstContext,
            legalProcess.Id,
            organization.Id,
            timeout.Token);
        firstProcess.ChangeTitle("Alpha");
        await firstContext.SaveChangesAsync(timeout.Token);

        Task<LegalProcessMutationPersistenceResult>? secondMutation = null;

        try
        {
            secondMutation = UpdateTitleAsync(
                CreatePersistence(),
                legalProcess.Id,
                organization.Id,
                "Beta",
                timeout.Token);

            await WaitForBlockedLegalProcessLockAsync(timeout.Token);

            Assert.False(secondMutation.IsCompleted);

            await firstTransaction.CommitAsync(timeout.Token);

            LegalProcessMutationPersistenceResult secondResult =
                await secondMutation.WaitAsync(timeout.Token);

            Assert.Equal(LegalProcessMutationPersistenceResult.Updated, secondResult);

            await using EnmaDbContext verificationContext = fixture.CreateDbContext();
            LegalProcess persistedProcess = await verificationContext.LegalProcesses
                .AsNoTracking()
                .SingleAsync(
                    candidate => candidate.Id == legalProcess.Id,
                    timeout.Token);

            Assert.Equal("Beta", persistedProcess.Title);
            Assert.Equal(organization.Id, persistedProcess.OrganizationId);
            Assert.Equal(client.Id, persistedProcess.ClientId);
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
    public async Task UpdateTitleAsync_WithDifferentProcess_DoesNotUseGlobalProcessLock()
    {
        Organization organization = CreateOrganization(
            "Independent Organization",
            "independent-process-organization");
        var client = new Client(organization.Id, "Independent Client", CreatedAt);
        var processA = new LegalProcess(
            organization.Id,
            client.Id,
            "Process A",
            CreatedAt);
        var processB = new LegalProcess(
            organization.Id,
            client.Id,
            "Process B",
            CreatedAt);
        await SeedAsync(organization, client, processA, processB);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await LockLegalProcessAsync(
            blockerContext,
            processA.Id,
            organization.Id,
            timeout.Token);

        try
        {
            LegalProcessMutationPersistenceResult result = await UpdateTitleAsync(
                    CreatePersistence(),
                    processB.Id,
                    organization.Id,
                    "Updated Process B",
                    timeout.Token)
                .WaitAsync(timeout.Token);

            Assert.Equal(LegalProcessMutationPersistenceResult.Updated, result);

            await using EnmaDbContext verificationContext = fixture.CreateDbContext();
            LegalProcess persistedProcessB = await verificationContext.LegalProcesses
                .AsNoTracking()
                .SingleAsync(
                    candidate => candidate.Id == processB.Id,
                    timeout.Token);

            Assert.Equal("Updated Process B", persistedProcessB.Title);
            Assert.Equal(organization.Id, persistedProcessB.OrganizationId);
            Assert.Equal(client.Id, persistedProcessB.ClientId);
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
    public async Task UpdateTitleAsync_WithWrongTenantWhileRowLocked_ReturnsNotFoundWithoutAcquiringRow()
    {
        Organization organizationA = CreateOrganization(
            "Wrong Context Organization",
            "wrong-context-organization");
        Organization organizationB = CreateOrganization(
            "Owning Organization",
            "owning-organization");
        var clientB = new Client(organizationB.Id, "Owning Client", CreatedAt);
        var processB = new LegalProcess(
            organizationB.Id,
            clientB.Id,
            "Protected title",
            CreatedAt);
        await SeedAsync(organizationA, organizationB, clientB, processB);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await LockLegalProcessAsync(
            blockerContext,
            processB.Id,
            organizationB.Id,
            timeout.Token);

        try
        {
            LegalProcessMutationPersistenceResult result = await UpdateTitleAsync(
                    CreatePersistence(),
                    processB.Id,
                    organizationA.Id,
                    "Cross-tenant title",
                    timeout.Token)
                .WaitAsync(timeout.Token);

            Assert.Equal(LegalProcessMutationPersistenceResult.NotFound, result);
        }
        finally
        {
            if (blockerTransaction.GetDbTransaction().Connection is not null)
            {
                await blockerTransaction.RollbackAsync(CancellationToken.None);
            }
        }

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        LegalProcess persistedProcessB = await verificationContext.LegalProcesses
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == processB.Id);

        Assert.Equal("Protected title", persistedProcessB.Title);
        Assert.Equal(organizationB.Id, persistedProcessB.OrganizationId);
        Assert.Equal(clientB.Id, persistedProcessB.ClientId);
    }

    [Fact]
    public async Task UpdateTitleAsync_CompetingSameEffectiveTitle_EmitsExactlyOneAudit()
    {
        Organization organization = CreateOrganization(
            "Concurrent audit organization",
            "concurrent-audit-process-organization");
        var client = new Client(organization.Id, "Audit client", CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            "Initial audit title",
            CreatedAt);
        await SeedAsync(organization, client, legalProcess);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        LegalProcessMutationPersistenceResult[] results = await Task.WhenAll(
            UpdateTitleAsync(
                CreatePersistence(),
                legalProcess.Id,
                organization.Id,
                "Same final title",
                timeout.Token),
            UpdateTitleAsync(
                CreatePersistence(),
                legalProcess.Id,
                organization.Id,
                " Same final title ",
                timeout.Token));

        Assert.All(results, result => Assert.Equal(
            LegalProcessMutationPersistenceResult.Updated,
            result));
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.Equal(
            "Same final title",
            await verificationContext.LegalProcesses
                .Where(candidate => candidate.Id == legalProcess.Id)
                .Select(candidate => candidate.Title)
                .SingleAsync(timeout.Token));
        AuditLog auditLog = await verificationContext.AuditLogs
            .AsNoTracking()
            .SingleAsync(timeout.Token);
        Assert.Equal(
            AuditEventType.LegalProcessTitleChanged,
            auditLog.EventType);
        Assert.Equal(legalProcess.Id, auditLog.EntityId);
        Assert.Null(auditLog.Details);
    }

    private LegalProcessMutationPersistence CreatePersistence()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;

        return new LegalProcessMutationPersistence(options, TimeProvider.System);
    }

    private Task<LegalProcessMutationPersistenceResult> UpdateTitleAsync(
        LegalProcessMutationPersistence persistence,
        Guid processId,
        Guid organizationId,
        string title,
        CancellationToken cancellationToken)
    {
        (Guid userId, Guid membershipId) = _actors[organizationId];
        return persistence.UpdateTitleAsync(
            new LegalProcessMutationPersistenceRequest(
                userId,
                organizationId,
                membershipId,
                processId),
            state =>
            {
                state.LegalProcess.ChangeTitle(title);
                return LegalProcessMutationDecision.Persist;
            },
            cancellationToken);
    }

    private async Task WaitForBlockedLegalProcessLockAsync(
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
                      AND query ILIKE '%FROM legal_processes%'
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

    private static async Task<LegalProcess> LockLegalProcessAsync(
        EnmaDbContext dbContext,
        Guid processId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        LegalProcess[] legalProcesses = await dbContext.LegalProcesses
            .FromSqlInterpolated(
                $"""
                SELECT * FROM legal_processes
                WHERE id = {processId}
                  AND organization_id = {organizationId}
                FOR UPDATE
                """)
            .ToArrayAsync(cancellationToken);

        return Assert.Single(legalProcesses);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        foreach (Organization organization in entities.OfType<Organization>())
        {
            var user = new User(
                "Concurrent process actor",
                $"process-concurrency-{organization.Id:N}@example.test",
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
