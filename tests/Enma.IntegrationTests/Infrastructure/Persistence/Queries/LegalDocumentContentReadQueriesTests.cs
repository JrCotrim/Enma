using System.Data.Common;
using System.Globalization;
using Enma.Application.Documents.Download;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Queries;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDocumentContentReadQueriesTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        20,
        16,
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
    public async Task FindAsync_UsesOneTenantQualifiedQueryForPersistedLocator()
    {
        DocumentGraph graphA = CreateGraph(
            "Content Alpha",
            "document-content-query-alpha");
        DocumentGraph graphB = CreateGraph(
            "Content Beta",
            "document-content-query-beta");
        LegalDocument documentA = CreateDocument(graphA, "alpha.pdf", 1);
        LegalDocument documentB = CreateDocument(graphB, "beta.pdf", 2);
        await SeedAsync(
            graphA.Entities
                .Concat(graphB.Entities)
                .Concat([documentA, documentB])
                .ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext =
            CreateQueryContext(interceptor);
        var queries = new LegalDocumentContentReadQueries(dbContext);

        LegalDocumentContentReadModel? sameTenant = await queries.FindAsync(
            graphA.Organization.Id,
            documentA.Id);
        LegalDocumentContentReadModel? foreignTenant = await queries.FindAsync(
            graphA.Organization.Id,
            documentB.Id);
        LegalDocumentContentReadModel? missing = await queries.FindAsync(
            graphA.Organization.Id,
            Guid.NewGuid());

        Assert.NotNull(sameTenant);
        Assert.Equal(documentA.Id, sameTenant.DocumentId);
        Assert.Equal(documentA.OriginalFileName, sameTenant.OriginalFileName);
        Assert.Equal(documentA.ContentType, sameTenant.ContentType);
        Assert.Equal(documentA.SizeBytes, sameTenant.SizeBytes);
        Assert.Equal(documentA.StoredObjectKey, sameTenant.StoredObjectKey);
        Assert.Null(foreignTenant);
        Assert.Null(missing);
        Assert.Equal(3, interceptor.CommandTexts.Count);
        Assert.All(
            interceptor.CommandTexts,
            command =>
            {
                Assert.Contains("organization_id", command);
                Assert.Contains("stored_object_key", command);
                Assert.Contains("WHERE", command);
                Assert.Contains("id", command);
                Assert.Contains("@", command);
            });
    }

    [Fact]
    public async Task FindAsync_WithPreCanceledToken_PropagatesCancellation()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new LegalDocumentContentReadQueries(dbContext);
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queries.FindAsync(
                Guid.NewGuid(),
                Guid.NewGuid(),
                cancellationTokenSource.Token));
    }

    private EnmaDbContext CreateQueryContext(
        DbCommandInterceptor interceptor)
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;
        return new EnmaDbContext(options);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static DocumentGraph CreateGraph(string name, string slug)
    {
        var organization = new Organization(name, slug, CreatedAt);
        var user = new User(
            $"{name} user",
            $"{slug}@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);
        return new DocumentGraph(organization, user, membership);
    }

    private static LegalDocument CreateDocument(
        DocumentGraph graph,
        string originalFileName,
        int key)
    {
        return new LegalDocument(
            graph.Organization.Id,
            null,
            null,
            originalFileName,
            key.ToString("x32", CultureInfo.InvariantCulture),
            "application/pdf",
            100 + key,
            new LegalDocumentContentHash(
                Enumerable.Repeat((byte)key, 32).ToArray()),
            graph.Membership.Id,
            CreatedAt.AddMinutes(key));
    }

    private sealed record DocumentGraph(
        Organization Organization,
        User User,
        OrganizationMembership Membership)
    {
        public object[] Entities => [Organization, User, Membership];
    }

    private sealed class ReaderCommandInterceptor : DbCommandInterceptor
    {
        private readonly List<string> commandTexts = [];

        public IReadOnlyList<string> CommandTexts => commandTexts;

        public override ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            commandTexts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
