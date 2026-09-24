using Enma.Application.Authorization;
using Enma.Application.Documents;
using Enma.Application.Documents.Delete;
using Enma.Application.Documents.Storage;
using Enma.Domain.Auditing;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Documents;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDocumentDeletionPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026, 9, 23, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DeletedAt = CreatedAt.AddHours(1);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DeleteAndProcessAsync_HidesImmediatelyAuditsOnceAndFinalizesMetadata()
    {
        SeedGraph graph = await SeedAsync(OrganizationRole.Owner);
        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        DeleteLegalDocumentUseCase useCase = CreateUseCase(
            authorizationContext);

        DeleteLegalDocumentResult first = await useCase.ExecuteAsync(
            new DeleteLegalDocumentCommand(
                graph.User.Id,
                graph.Organization.Id,
                graph.Document.Id));
        DeleteLegalDocumentResult duplicate = await useCase.ExecuteAsync(
            new DeleteLegalDocumentCommand(
                graph.User.Id,
                graph.Organization.Id,
                graph.Document.Id));

        Assert.Equal(DeleteLegalDocumentResultStatus.Accepted, first.Status);
        Assert.Equal(DeleteLegalDocumentResultStatus.Accepted, duplicate.Status);

        await using (EnmaDbContext queryContext = fixture.CreateDbContext())
        {
            var metadataQueries = new LegalDocumentReadQueries(queryContext);
            var contentQueries = new LegalDocumentContentReadQueries(queryContext);

            Assert.Null(await metadataQueries.FindAsync(
                graph.Document.Id,
                graph.Organization.Id));
            Assert.Null(await contentQueries.FindAsync(
                graph.Organization.Id,
                graph.Document.Id));
            Assert.Empty((await metadataQueries.ListAsync(
                new LegalDocumentListReadRequest(
                    graph.Organization.Id,
                    null,
                    null,
                    null,
                    1,
                    20))).Items);

            AuditLog audit = await queryContext.AuditLogs
                .AsNoTracking()
                .SingleAsync();
            Assert.Equal(AuditEventType.LegalDocumentDeleted, audit.EventType);
            Assert.Equal(graph.Document.Id, audit.EntityId);
            Assert.Equal(graph.Membership.Id, audit.ActorMembershipId);
            Assert.Equal(DeletedAt, audit.OccurredAt);
        }

        var persistence = CreatePersistence();
        var storage = new RecordingStorage();
        var processor = new ProcessLegalDocumentDeletionsUseCase(
            persistence,
            storage);

        ProcessLegalDocumentDeletionsResult processed =
            await processor.ExecuteAsync();

        Assert.Equal(new(1, 1, 0), processed);
        Assert.Equal(
            [graph.Document.StoredObjectKey],
            storage.DeletedKeys.Select(key => key.Value));
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.False(await verificationContext.LegalDocuments.AnyAsync());
        Assert.Single(await verificationContext.AuditLogs.ToArrayAsync());
    }

    [Theory]
    [InlineData(OrganizationRole.Member)]
    public async Task DeleteAsync_WithoutPrivilegedRole_DeniesWithoutMutation(
        OrganizationRole role)
    {
        SeedGraph graph = await SeedAsync(role);
        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        DeleteLegalDocumentUseCase useCase = CreateUseCase(
            authorizationContext);

        DeleteLegalDocumentResult result = await useCase.ExecuteAsync(
            new DeleteLegalDocumentCommand(
                graph.User.Id,
                graph.Organization.Id,
                graph.Document.Id));

        Assert.Equal(DeleteLegalDocumentResultStatus.AccessDenied, result.Status);
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        LegalDocument document = await verificationContext.LegalDocuments
            .AsNoTracking()
            .SingleAsync();
        Assert.Null(document.DeletionRequestedAt);
        Assert.False(await verificationContext.AuditLogs.AnyAsync());
    }

    [Fact]
    public async Task DeleteAsync_WithConcurrentDuplicateRequests_AuditsOnce()
    {
        SeedGraph graph = await SeedAsync(OrganizationRole.Administrator);
        await using EnmaDbContext firstAuthorizationContext =
            fixture.CreateDbContext();
        await using EnmaDbContext secondAuthorizationContext =
            fixture.CreateDbContext();
        DeleteLegalDocumentCommand command = new(
            graph.User.Id,
            graph.Organization.Id,
            graph.Document.Id);

        DeleteLegalDocumentResult[] results = await Task.WhenAll(
            CreateUseCase(firstAuthorizationContext).ExecuteAsync(command),
            CreateUseCase(secondAuthorizationContext).ExecuteAsync(command));

        Assert.All(
            results,
            result => Assert.Equal(
                DeleteLegalDocumentResultStatus.Accepted,
                result.Status));
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        LegalDocument document = await verificationContext.LegalDocuments
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(DeletedAt, document.DeletionRequestedAt);
        Assert.Equal(1, await verificationContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task DeleteAsync_WithForeignTenantDocument_ReturnsNotFoundWithoutMutation()
    {
        SeedGraph actor = await SeedAsync(OrganizationRole.Owner);
        SeedGraph foreign = await SeedAsync(
            OrganizationRole.Owner,
            "fedcba9876543210fedcba9876543210");
        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        DeleteLegalDocumentUseCase useCase = CreateUseCase(
            authorizationContext);

        DeleteLegalDocumentResult result = await useCase.ExecuteAsync(
            new DeleteLegalDocumentCommand(
                actor.User.Id,
                actor.Organization.Id,
                foreign.Document.Id));

        Assert.Equal(DeleteLegalDocumentResultStatus.NotFound, result.Status);
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.Equal(
            2,
            await verificationContext.LegalDocuments.CountAsync(
                document => document.DeletionRequestedAt == null));
        Assert.False(await verificationContext.AuditLogs.AnyAsync());
    }

    private DeleteLegalDocumentUseCase CreateUseCase(
        EnmaDbContext authorizationContext) => new(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(authorizationContext)),
            CreatePersistence(),
            new FixedTimeProvider(DeletedAt));

    private LegalDocumentDeletionPersistence CreatePersistence()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;
        return new LegalDocumentDeletionPersistence(
            options,
            new FixedTimeProvider(DeletedAt));
    }

    private async Task<SeedGraph> SeedAsync(
        OrganizationRole role,
        string storedObjectKey = "0123456789abcdef0123456789abcdef")
    {
        var organization = new Organization(
            "Deletion Organization",
            $"deletion-{Guid.NewGuid():N}",
            CreatedAt);
        var user = new User(
            "Deletion User",
            $"deletion-{Guid.NewGuid():N}@example.com",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            CreatedAt);
        var document = new LegalDocument(
            organization.Id,
            null,
            null,
            "contract.pdf",
            storedObjectKey,
            "application/pdf",
            128,
            new LegalDocumentContentHash(Enumerable.Range(0, 32)
                .Select(value => (byte)value)
                .ToArray()),
            membership.Id,
            CreatedAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(organization, user, membership, document);
        await dbContext.SaveChangesAsync();
        return new SeedGraph(organization, user, membership, document);
    }

    private sealed record SeedGraph(
        Organization Organization,
        User User,
        OrganizationMembership Membership,
        LegalDocument Document);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingStorage : ILegalDocumentStorage
    {
        public List<LegalDocumentStorageObjectKey> DeletedKeys { get; } = [];

        public Task StoreAsync(
            LegalDocumentStorageObjectKey objectKey,
            Stream content,
            long contentLength,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ILegalDocumentStorageReadHandle> OpenReadAsync(
            LegalDocumentStorageObjectKey objectKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteIfExistsAsync(
            LegalDocumentStorageObjectKey objectKey,
            CancellationToken cancellationToken = default)
        {
            DeletedKeys.Add(objectKey);
            return Task.CompletedTask;
        }
    }
}
