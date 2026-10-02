using System.Data;
using Enma.Application.Clients;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

// The duplicate-document pre-check runs under the organization row lock. These
// tests use a writer that does not take that lock, so its uncommitted row is
// invisible to the pre-check and only the unique index can reject the write.
[Collection(PostgreSqlCollection.Name)]
public sealed class ClientDocumentUniquenessPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string Cpf = "52998224725";
    private const string Cnpj = "12ABC34501DE35";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        10,
        1,
        12,
        0,
        0,
        TimeSpan.Zero);

    private Guid _userId;
    private Guid _membershipId;

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(PersonType.Individual, Cpf, null)]
    [InlineData(PersonType.Company, null, Cnpj)]
    public async Task ExecuteAsync_UniqueIndexRejectsRacingDocument_ReturnsDuplicateDocumentWithoutWrites(
        PersonType personType,
        string? cpf,
        string? cnpj)
    {
        Organization organization = await SeedOrganizationAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginBlockingDocumentClaimAsync(
                blockerContext,
                organization,
                personType,
                cpf,
                cnpj,
                timeout.Token);
        var persistence = new ClientCreationPersistence(
            CreateOptions(),
            TimeProvider.System);
        var request = new ClientCreationPersistenceRequest(
            _userId,
            organization.Id,
            _membershipId);
        Task<ClientCreationPersistenceResult>? creation = null;

        try
        {
            creation = persistence.ExecuteAsync(
                request,
                _ => ClientCreationDecision.Persist(new Client(
                    organization.Id,
                    "Racing client",
                    CreatedAt,
                    cpf: cpf,
                    personType: personType,
                    cnpj: cnpj)),
                timeout.Token);

            await WaitForBlockedClientWriteAsync(timeout.Token);
            Assert.False(creation.IsCompleted);
            await blockerTransaction.CommitAsync(timeout.Token);

            ClientCreationPersistenceResult result =
                await creation.WaitAsync(timeout.Token);

            Assert.Equal(ClientCreationDecisionStatus.DuplicateDocument, result.Status);
            Assert.Null(result.ClientId);
            await using EnmaDbContext verificationContext = fixture.CreateDbContext();
            string persistedName = Assert.Single(
                await verificationContext.Clients
                    .AsNoTracking()
                    .Select(client => client.Name)
                    .ToArrayAsync(timeout.Token));
            Assert.Equal("Blocking client", persistedName);
            Assert.Equal(
                0,
                await verificationContext.AuditLogs.CountAsync(timeout.Token));
        }
        finally
        {
            await RollbackIfOpenAsync(blockerTransaction);
            await DrainTaskAsync(creation);
        }
    }

    [Theory]
    [InlineData(PersonType.Individual, Cpf, null)]
    [InlineData(PersonType.Company, null, Cnpj)]
    public async Task UpdateNameAsync_UniqueIndexRejectsRacingDocument_ReturnsDuplicateDocumentWithoutWrites(
        PersonType personType,
        string? cpf,
        string? cnpj)
    {
        Organization organization = await SeedOrganizationAsync();
        var existingClient = new Client(
            organization.Id,
            "Existing client",
            CreatedAt,
            personType: personType);
        await SeedAsync(existingClient);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await BeginBlockingDocumentClaimAsync(
                blockerContext,
                organization,
                personType,
                cpf,
                cnpj,
                timeout.Token);
        var persistence = new ClientMutationPersistence(
            CreateOptions(),
            TimeProvider.System);
        var request = new ClientMutationPersistenceRequest(
            _userId,
            organization.Id,
            _membershipId,
            existingClient.Id);
        Task<ClientMutationPersistenceResult>? update = null;

        try
        {
            update = persistence.UpdateNameAsync(
                request,
                state =>
                {
                    state.Client.UpdateProfile(
                        "Renamed client",
                        null,
                        null,
                        cpf,
                        personType,
                        cnpj,
                        null,
                        null);
                    return ClientMutationDecision.Persist;
                },
                timeout.Token);

            await WaitForBlockedClientWriteAsync(timeout.Token);
            Assert.False(update.IsCompleted);
            await blockerTransaction.CommitAsync(timeout.Token);

            ClientMutationPersistenceResult result =
                await update.WaitAsync(timeout.Token);

            Assert.Equal(ClientMutationPersistenceResult.DuplicateDocument, result);
            await using EnmaDbContext verificationContext = fixture.CreateDbContext();
            Client persisted = await verificationContext.Clients
                .AsNoTracking()
                .SingleAsync(
                    client => client.Id == existingClient.Id,
                    timeout.Token);
            Assert.Equal("Existing client", persisted.Name);
            Assert.Null(persisted.Cpf);
            Assert.Null(persisted.Cnpj);
            Assert.Equal(
                0,
                await verificationContext.AuditLogs.CountAsync(timeout.Token));
        }
        finally
        {
            await RollbackIfOpenAsync(blockerTransaction);
            await DrainTaskAsync(update);
        }
    }

    // Inserting a client would take a FOR KEY SHARE lock on the organization
    // row (foreign key) and so serialize with the organization lock. Updating
    // the document of an existing client does not, so its uncommitted claim
    // stays invisible to the pre-check and only the unique index rejects it.
    private async Task<IDbContextTransaction> BeginBlockingDocumentClaimAsync(
        EnmaDbContext dbContext,
        Organization organization,
        PersonType personType,
        string? cpf,
        string? cnpj,
        CancellationToken cancellationToken)
    {
        var blockingClient = new Client(
            organization.Id,
            "Blocking client",
            CreatedAt,
            personType: personType);
        await SeedAsync(blockingClient);
        IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
        Client trackedClient = await dbContext.Clients.SingleAsync(
            client => client.Id == blockingClient.Id,
            cancellationToken);
        trackedClient.UpdateProfile(
            trackedClient.Name,
            null,
            null,
            cpf,
            personType,
            cnpj,
            null,
            null);
        await dbContext.SaveChangesAsync(cancellationToken);
        return transaction;
    }

    private async Task WaitForBlockedClientWriteAsync(
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
                      AND (query ILIKE '%INSERT INTO clients%'
                        OR query ILIKE '%UPDATE clients%')
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

    private async Task<Organization> SeedOrganizationAsync()
    {
        var organization = new Organization(
            "Document Uniqueness Organization",
            $"document-uniqueness-{Guid.NewGuid():N}",
            CreatedAt);
        var user = new User(
            "Document uniqueness actor",
            $"document-uniqueness-{Guid.NewGuid():N}@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);
        await SeedAsync(organization, user, membership);
        _userId = user.Id;
        _membershipId = membership.Id;
        return organization;
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private DbContextOptions<EnmaDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
    }

    private static async Task RollbackIfOpenAsync(IDbContextTransaction transaction)
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
