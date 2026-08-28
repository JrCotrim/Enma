using System.Data;
using Enma.Application.Documents.Storage;
using Enma.Application.Documents.Upload;
using Enma.Domain.Clients;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDocumentMetadataUploadConcurrencyTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        19,
        21,
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
    public async Task ExecuteAsync_WhenUploadLocksActorFirst_ConcurrentMembershipDeactivationWaits()
    {
        SeedGraph graph = await SeedGraphAsync();
        LegalDocumentUploadPersistenceRequest request =
            CreateRequest(graph);

        await using EnmaDbContext deactivationContext =
            fixture.CreateDbContext();
        OrganizationMembership membershipToDeactivate =
            await deactivationContext.OrganizationMemberships
                .SingleAsync(item => item.Id == graph.Membership.Id);

        await using EnmaDbContext blockerContext =
            fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted);

        await blockerContext.Organizations
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organizations
                WHERE id = {graph.Organization.Id}
                FOR UPDATE
                """)
            .SingleAsync();

        LegalDocumentMetadataUploadTransaction uploadTransaction =
            CreateUploadTransaction();
        var attempt = new LegalDocumentMetadataUploadAttempt();

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));

        Task<LegalDocumentUploadPersistenceResult> uploadTask =
            uploadTransaction.ExecuteAsync(
                request,
                _ => LegalDocumentUploadDecision.Persist(
                    CreateDocument(request)),
                attempt,
                timeout.Token);

        Task<int>? deactivationTask = null;
        bool blockerReleased = false;

        try
        {
            await WaitForBlockedCommandAsync(
                "SELECT",
                "organizations",
                timeout.Token);

            membershipToDeactivate.Deactivate();
            deactivationTask =
                deactivationContext.SaveChangesAsync(timeout.Token);

            await WaitForBlockedCommandAsync(
                "UPDATE",
                "organization_memberships",
                timeout.Token);

            Assert.False(deactivationTask.IsCompleted);

            await blockerTransaction.CommitAsync(timeout.Token);
            blockerReleased = true;

            LegalDocumentUploadPersistenceResult result =
                await uploadTask;
            await deactivationTask;

            Assert.True(attempt.CommitStarted);
            Assert.Equal(
                LegalDocumentUploadPersistenceResultStatus.Persisted,
                result.Status);

            await using EnmaDbContext verificationContext =
                fixture.CreateDbContext();

            LegalDocument document = await verificationContext.LegalDocuments
                .AsNoTracking()
                .SingleAsync();

            Assert.Equal(graph.Membership.Id, document.UploadedByMembershipId);
            Assert.False(
                await verificationContext.OrganizationMemberships
                    .Where(item => item.Id == graph.Membership.Id)
                    .Select(item => item.IsActive)
                    .SingleAsync());
        }
        finally
        {
            if (!blockerReleased)
            {
                await TryRollbackAsync(blockerTransaction);
            }

            await ObserveTaskAsync(uploadTask);

            if (deactivationTask is not null)
            {
                await ObserveTaskAsync(deactivationTask);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenUploadLocksClientFirst_ConcurrentClientDeactivationWaitsThenBecomesHistorical()
    {
        SeedGraph graph = await SeedGraphAsync(includeClient: true);
        LegalDocumentUploadPersistenceRequest request =
            CreateRequest(
                graph,
                clientId: graph.Client!.Id);

        await using EnmaDbContext deactivationContext =
            fixture.CreateDbContext();
        Client clientToDeactivate = await deactivationContext.Clients
            .SingleAsync(item => item.Id == graph.Client.Id);

        await using EnmaDbContext blockerContext =
            fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted);

        await blockerContext.OrganizationMemberships
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organization_memberships
                WHERE organization_id = {graph.Organization.Id}
                  AND id = {graph.Membership.Id}
                FOR UPDATE
                """)
            .SingleAsync();

        LegalDocumentMetadataUploadTransaction uploadTransaction =
            CreateUploadTransaction();
        var attempt = new LegalDocumentMetadataUploadAttempt();

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));

        Task<LegalDocumentUploadPersistenceResult> uploadTask =
            uploadTransaction.ExecuteAsync(
                request,
                _ => LegalDocumentUploadDecision.Persist(
                    CreateDocument(request)),
                attempt,
                timeout.Token);

        Task<int>? deactivationTask = null;
        bool blockerReleased = false;

        try
        {
            await WaitForBlockedCommandAsync(
                "SELECT",
                "organization_memberships",
                timeout.Token);

            clientToDeactivate.Deactivate();
            deactivationTask =
                deactivationContext.SaveChangesAsync(timeout.Token);

            await WaitForBlockedCommandAsync(
                "UPDATE",
                "clients",
                timeout.Token);

            Assert.False(deactivationTask.IsCompleted);

            await blockerTransaction.CommitAsync(timeout.Token);
            blockerReleased = true;

            LegalDocumentUploadPersistenceResult result =
                await uploadTask;
            await deactivationTask;

            Assert.True(attempt.CommitStarted);
            Assert.Equal(
                LegalDocumentUploadPersistenceResultStatus.Persisted,
                result.Status);

            await using EnmaDbContext verificationContext =
                fixture.CreateDbContext();

            LegalDocument document = await verificationContext.LegalDocuments
                .AsNoTracking()
                .SingleAsync();

            Assert.Equal(graph.Client.Id, document.ClientId);
            Assert.False(
                await verificationContext.Clients
                    .Where(item => item.Id == graph.Client.Id)
                    .Select(item => item.IsActive)
                    .SingleAsync());
        }
        finally
        {
            if (!blockerReleased)
            {
                await TryRollbackAsync(blockerTransaction);
            }

            await ObserveTaskAsync(uploadTask);

            if (deactivationTask is not null)
            {
                await ObserveTaskAsync(deactivationTask);
            }
        }
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("user")]
    [InlineData("organization")]
    [InlineData("role")]
    public async Task ExecuteAsync_WhenConcurrentActorMutationWins_UsesCurrentLockedState(
        string mutation)
    {
        SeedGraph graph = await SeedGraphAsync();
        LegalDocumentUploadPersistenceRequest request = CreateRequest(graph);

        await using EnmaDbContext mutationContext = fixture.CreateDbContext();
        await using IDbContextTransaction mutationTransaction =
            await mutationContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted);

        await ApplyActorMutationAsync(
            mutationContext,
            graph,
            mutation);

        LegalDocumentMetadataUploadTransaction uploadTransaction =
            CreateUploadTransaction();
        var attempt = new LegalDocumentMetadataUploadAttempt();
        LegalDocumentUploadActorState? observedActor = null;

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));

        Task<LegalDocumentUploadPersistenceResult> uploadTask =
            uploadTransaction.ExecuteAsync(
                request,
                state =>
                {
                    observedActor = state.Actor;

                    return mutation == "role"
                        ? LegalDocumentUploadDecision.Persist(
                            CreateDocument(request))
                        : LegalDocumentUploadDecision.AccessDenied;
                },
                attempt,
                timeout.Token);

        bool mutationCommitted = false;

        try
        {
            await WaitForBlockedCommandAsync(
                "SELECT",
                GetActorMutationTableName(mutation),
                timeout.Token);

            Assert.False(uploadTask.IsCompleted);

            await mutationTransaction.CommitAsync(timeout.Token);
            mutationCommitted = true;

            LegalDocumentUploadPersistenceResult result = await uploadTask;
            LegalDocumentUploadActorState actor =
                Assert.IsType<LegalDocumentUploadActorState>(observedActor);

            switch (mutation)
            {
                case "membership":
                    Assert.False(actor.IsMembershipActive);
                    Assert.Equal(
                        LegalDocumentUploadPersistenceResultStatus.AccessDenied,
                        result.Status);
                    Assert.False(attempt.CommitStarted);
                    break;
                case "user":
                    Assert.False(actor.IsUserActive);
                    Assert.Equal(
                        LegalDocumentUploadPersistenceResultStatus.AccessDenied,
                        result.Status);
                    Assert.False(attempt.CommitStarted);
                    break;
                case "organization":
                    Assert.False(actor.IsOrganizationActive);
                    Assert.Equal(
                        LegalDocumentUploadPersistenceResultStatus.AccessDenied,
                        result.Status);
                    Assert.False(attempt.CommitStarted);
                    break;
                case "role":
                    Assert.Equal(OrganizationRole.Member, actor.Role);
                    Assert.Equal(
                        LegalDocumentUploadPersistenceResultStatus.Persisted,
                        result.Status);
                    Assert.True(attempt.CommitStarted);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported actor mutation '{mutation}'.");
            }

            await using EnmaDbContext verificationContext =
                fixture.CreateDbContext();

            int documentCount =
                await verificationContext.LegalDocuments.CountAsync();

            Assert.Equal(
                mutation == "role" ? 1 : 0,
                documentCount);
        }
        finally
        {
            if (!mutationCommitted)
            {
                await TryRollbackAsync(mutationTransaction);
            }

            await ObserveTaskAsync(uploadTask);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenConcurrentClientDeactivationWins_UsesInactiveClientAndRejects()
    {
        SeedGraph graph = await SeedGraphAsync(includeClient: true);
        LegalDocumentUploadPersistenceRequest request =
            CreateRequest(graph, clientId: graph.Client!.Id);

        await using EnmaDbContext deactivationContext =
            fixture.CreateDbContext();
        await using IDbContextTransaction deactivationTransaction =
            await deactivationContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted);

        Client client = await deactivationContext.Clients
            .SingleAsync(item => item.Id == graph.Client.Id);
        client.Deactivate();
        await deactivationContext.SaveChangesAsync();

        LegalDocumentMetadataUploadTransaction uploadTransaction =
            CreateUploadTransaction();
        var attempt = new LegalDocumentMetadataUploadAttempt();
        LegalDocumentUploadClientState? observedClient = null;

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));

        Task<LegalDocumentUploadPersistenceResult> uploadTask =
            uploadTransaction.ExecuteAsync(
                request,
                state =>
                {
                    observedClient = state.Client;
                    return LegalDocumentUploadDecision.RelatedClientUnavailable;
                },
                attempt,
                timeout.Token);

        bool deactivationCommitted = false;

        try
        {
            await WaitForBlockedCommandAsync(
                "SELECT",
                "clients",
                timeout.Token);

            Assert.False(uploadTask.IsCompleted);

            await deactivationTransaction.CommitAsync(timeout.Token);
            deactivationCommitted = true;

            LegalDocumentUploadPersistenceResult result = await uploadTask;
            LegalDocumentUploadClientState lockedClient =
                Assert.IsType<LegalDocumentUploadClientState>(observedClient);

            Assert.False(lockedClient.IsActive);
            Assert.False(attempt.CommitStarted);
            Assert.Equal(
                LegalDocumentUploadPersistenceResultStatus.RelatedClientUnavailable,
                result.Status);

            await using EnmaDbContext verificationContext =
                fixture.CreateDbContext();
            Assert.Equal(
                0,
                await verificationContext.LegalDocuments.CountAsync());
        }
        finally
        {
            if (!deactivationCommitted)
            {
                await TryRollbackAsync(deactivationTransaction);
            }

            await ObserveTaskAsync(uploadTask);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UnrelatedOrganizations_DoNotSerializeBehindGlobalLock()
    {
        SeedGraph blockedGraph = await SeedGraphAsync(
            organizationName: "Blocked Organization",
            organizationSlug: "documents-concurrency-blocked",
            userEmail: "documents-concurrency-blocked@example.com");
        SeedGraph independentGraph = await SeedGraphAsync(
            organizationName: "Independent Organization",
            organizationSlug: "documents-concurrency-independent",
            userEmail: "documents-concurrency-independent@example.com");

        LegalDocumentUploadPersistenceRequest blockedRequest =
            CreateRequest(blockedGraph);
        LegalDocumentUploadPersistenceRequest independentRequest =
            CreateRequest(independentGraph);

        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted);

        await blockerContext.Organizations
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organizations
                WHERE id = {blockedGraph.Organization.Id}
                FOR UPDATE
                """)
            .SingleAsync();

        LegalDocumentMetadataUploadTransaction blockedUploadTransaction =
            CreateUploadTransaction();
        LegalDocumentMetadataUploadTransaction independentUploadTransaction =
            CreateUploadTransaction();
        var blockedAttempt = new LegalDocumentMetadataUploadAttempt();
        var independentAttempt = new LegalDocumentMetadataUploadAttempt();

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));

        Task<LegalDocumentUploadPersistenceResult> blockedUploadTask =
            blockedUploadTransaction.ExecuteAsync(
                blockedRequest,
                _ => LegalDocumentUploadDecision.Persist(
                    CreateDocument(blockedRequest)),
                blockedAttempt,
                timeout.Token);

        Task<LegalDocumentUploadPersistenceResult>? independentUploadTask = null;
        bool blockerReleased = false;

        try
        {
            await WaitForBlockedCommandAsync(
                "SELECT",
                "organizations",
                timeout.Token);

            independentUploadTask = independentUploadTransaction.ExecuteAsync(
                independentRequest,
                _ => LegalDocumentUploadDecision.Persist(
                    CreateDocument(independentRequest)),
                independentAttempt,
                timeout.Token);

            LegalDocumentUploadPersistenceResult independentResult =
                await independentUploadTask.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    timeout.Token);

            Assert.Equal(
                LegalDocumentUploadPersistenceResultStatus.Persisted,
                independentResult.Status);
            Assert.True(independentAttempt.CommitStarted);
            Assert.False(blockedUploadTask.IsCompleted);

            await blockerTransaction.CommitAsync(timeout.Token);
            blockerReleased = true;

            LegalDocumentUploadPersistenceResult blockedResult =
                await blockedUploadTask;

            Assert.Equal(
                LegalDocumentUploadPersistenceResultStatus.Persisted,
                blockedResult.Status);
            Assert.True(blockedAttempt.CommitStarted);

            await using EnmaDbContext verificationContext =
                fixture.CreateDbContext();
            Assert.Equal(
                2,
                await verificationContext.LegalDocuments.CountAsync());
        }
        finally
        {
            if (!blockerReleased)
            {
                await TryRollbackAsync(blockerTransaction);
            }

            await ObserveTaskAsync(blockedUploadTask);

            if (independentUploadTask is not null)
            {
                await ObserveTaskAsync(independentUploadTask);
            }
        }
    }

    private LegalDocumentMetadataUploadTransaction CreateUploadTransaction()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;

        return new LegalDocumentMetadataUploadTransaction(
            options,
            TimeProvider.System);
    }

    private async Task WaitForBlockedCommandAsync(
        string verb,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var connection =
            new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        while (true)
        {
            await using NpgsqlCommand command = connection.CreateCommand();

            command.CommandText =
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND wait_event_type = 'Lock'
                      AND query ILIKE @verb_pattern
                      AND query ILIKE @table_pattern
                );
                """;

            command.Parameters.AddWithValue(
                "verb_pattern",
                $"%{verb}%");
            command.Parameters.AddWithValue(
                "table_pattern",
                $"%{tableName}%");

            object? value =
                await command.ExecuteScalarAsync(cancellationToken);

            if (value is true)
            {
                return;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(20),
                cancellationToken);
        }
    }

    private async Task<SeedGraph> SeedGraphAsync(
        bool includeClient = false,
        string organizationName = "Concurrency",
        string organizationSlug = "documents-concurrency",
        string userEmail = "documents-concurrency@example.com")
    {
        var organization = new Organization(
            organizationName,
            organizationSlug,
            CreatedAt);
        var user = new User(
            "Concurrency User",
            userEmail,
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);

        Client? client = includeClient
            ? new Client(
                organization.Id,
                "Concurrency Client",
                CreatedAt)
            : null;

        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        dbContext.Organizations.Add(organization);
        dbContext.Users.Add(user);
        dbContext.OrganizationMemberships.Add(membership);

        if (client is not null)
        {
            dbContext.Clients.Add(client);
        }

        await dbContext.SaveChangesAsync();

        return new SeedGraph(
            organization,
            user,
            membership,
            client);
    }

    private static async Task ApplyActorMutationAsync(
        EnmaDbContext dbContext,
        SeedGraph graph,
        string mutation)
    {
        switch (mutation)
        {
            case "membership":
            {
                OrganizationMembership membership =
                    await dbContext.OrganizationMemberships
                        .SingleAsync(item => item.Id == graph.Membership.Id);
                membership.Deactivate();
                break;
            }
            case "user":
            {
                User user = await dbContext.Users
                    .SingleAsync(item => item.Id == graph.User.Id);
                user.Deactivate();
                break;
            }
            case "organization":
            {
                Organization organization = await dbContext.Organizations
                    .SingleAsync(item => item.Id == graph.Organization.Id);
                organization.Deactivate();
                break;
            }
            case "role":
            {
                OrganizationMembership membership =
                    await dbContext.OrganizationMemberships
                        .SingleAsync(item => item.Id == graph.Membership.Id);
                membership.ChangeRole(OrganizationRole.Member);
                break;
            }
            default:
                throw new InvalidOperationException(
                    $"Unsupported actor mutation '{mutation}'.");
        }

        await dbContext.SaveChangesAsync();
    }

    private static string GetActorMutationTableName(string mutation)
    {
        return mutation switch
        {
            "membership" => "organization_memberships",
            "user" => "users",
            "organization" => "organizations",
            "role" => "organization_memberships",
            _ => throw new InvalidOperationException(
                $"Unsupported actor mutation '{mutation}'.")
        };
    }

    private static LegalDocumentUploadPersistenceRequest CreateRequest(
        SeedGraph graph,
        Guid? clientId = null)
    {
        return new LegalDocumentUploadPersistenceRequest(
            graph.User.Id,
            graph.Organization.Id,
            graph.Membership.Id,
            clientId,
            null,
            "concurrency.pdf",
            LegalDocumentStorageObjectKey.CreateNew(),
            "application/pdf",
            128,
            CreateHash());
    }

    private static LegalDocument CreateDocument(
        LegalDocumentUploadPersistenceRequest request)
    {
        return new LegalDocument(
            request.OrganizationId,
            request.ClientId,
            request.ProcessId,
            request.OriginalFileName,
            request.ObjectKey.Value,
            request.CanonicalContentType,
            request.ContentLength,
            request.ContentHashSha256,
            request.ActorMembershipId,
            CreatedAt);
    }

    private static LegalDocumentContentHash CreateHash()
    {
        return new LegalDocumentContentHash(
            Enumerable.Range(0, 32)
                .Select(index => (byte)index)
                .ToArray());
    }

    private static async Task TryRollbackAsync(
        IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch
        {
            // Best-effort test cleanup only.
        }
    }

    private static async Task ObserveTaskAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Preserve the primary assertion failure while ensuring the task is observed.
        }
    }


    private sealed record SeedGraph(
        Organization Organization,
        User User,
        OrganizationMembership Membership,
        Client? Client);
}
