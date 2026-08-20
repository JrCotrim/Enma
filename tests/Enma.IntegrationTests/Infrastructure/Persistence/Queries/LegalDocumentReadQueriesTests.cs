using System.Data.Common;
using System.Globalization;
using Enma.Application.Documents;
using Enma.Domain.Clients;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Queries;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDocumentReadQueriesTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        20,
        12,
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
    public async Task ListAsync_WithLiteralSearchAndRelations_UsesTenantQualifiedProjectedQueries()
    {
        DocumentGraph graphA = CreateGraph(
            "Alpha",
            "document-query-alpha",
            includeRelations: true);
        DocumentGraph graphB = CreateGraph(
            "Beta",
            "document-query-beta",
            includeRelations: true);
        LegalDocument percent = CreateDocument(
            graphA,
            "fee%report.pdf",
            1);
        LegalDocument underscore = CreateDocument(
            graphA,
            "draft_v1.pdf",
            2);
        LegalDocument uppercase = CreateDocument(
            graphA,
            "CASE SUMMARY.PDF",
            3);
        LegalDocument directClient = CreateDocument(
            graphA,
            "client-direct.pdf",
            4,
            clientId: graphA.Client!.Id);
        LegalDocument clientProcess = CreateDocument(
            graphA,
            "client-process.pdf",
            5,
            processId: graphA.Process!.Id);
        LegalDocument otherProcess = CreateDocument(
            graphA,
            "other-process.pdf",
            6,
            processId: graphA.OtherProcess!.Id);
        LegalDocument foreign = CreateDocument(
            graphB,
            "foreign-only.pdf",
            7);
        await SeedAsync(
            graphA.Entities
                .Concat(graphB.Entities)
                .Concat(
                [
                    percent,
                    underscore,
                    uppercase,
                    directClient,
                    clientProcess,
                    otherProcess,
                    foreign
                ])
                .ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext =
            CreateQueryContext(interceptor);
        var queries = new LegalDocumentReadQueries(dbContext);

        LegalDocumentListReadPage caseResults = await queries.ListAsync(
            CreateRequest(graphA.Organization.Id, search: "case summary"));
        LegalDocumentListReadPage percentResults = await queries.ListAsync(
            CreateRequest(graphA.Organization.Id, search: "%"));
        LegalDocumentListReadPage underscoreResults = await queries.ListAsync(
            CreateRequest(graphA.Organization.Id, search: "_"));
        LegalDocumentListReadPage backslashResults = await queries.ListAsync(
            CreateRequest(graphA.Organization.Id, search: "\\"));
        LegalDocumentListReadPage clientResults = await queries.ListAsync(
            CreateRequest(
                graphA.Organization.Id,
                clientId: graphA.Client.Id));
        LegalDocumentListReadPage processResults = await queries.ListAsync(
            CreateRequest(
                graphA.Organization.Id,
                processId: graphA.Process.Id));
        LegalDocumentListReadPage foreignResults = await queries.ListAsync(
            CreateRequest(graphA.Organization.Id, search: foreign.OriginalFileName));

        Assert.Equal(uppercase.Id, Assert.Single(caseResults.Items).Id);
        Assert.Equal(percent.Id, Assert.Single(percentResults.Items).Id);
        Assert.Equal(underscore.Id, Assert.Single(underscoreResults.Items).Id);
        Assert.Empty(backslashResults.Items);
        Assert.Equal(
            new[] { directClient.Id, clientProcess.Id }.OrderBy(id => id),
            clientResults.Items.Select(item => item.Id).OrderBy(id => id));
        Assert.Equal(
            clientProcess.Id,
            Assert.Single(processResults.Items).Id);
        Assert.Empty(foreignResults.Items);
        Assert.Equal(7, interceptor.CommandTexts.Count);

        string searchCommand = interceptor.CommandTexts[0];
        Assert.Contains("organization_id", searchCommand);
        Assert.Contains("ILIKE", searchCommand);
        Assert.Contains("ESCAPE", searchCommand);
        Assert.Contains("ORDER BY", searchCommand);
        Assert.Contains("DESC", searchCommand);
        Assert.Contains("LIMIT", searchCommand);
        Assert.Contains("OFFSET", searchCommand);
        Assert.Contains("@", searchCommand);
        Assert.DoesNotContain(uppercase.OriginalFileName, searchCommand);
        Assert.All(
            interceptor.CommandTexts,
            command => Assert.DoesNotContain(
                "stored_object_key",
                command,
                StringComparison.OrdinalIgnoreCase));

        string clientCommand = interceptor.CommandTexts[4];
        Assert.Contains("legal_processes", clientCommand);
        Assert.Contains("EXISTS", clientCommand);
        Assert.Contains("organization_id", clientCommand);
    }

    [Fact]
    public async Task ListAsync_WithMultiplePages_UsesCreatedAtThenIdDescending()
    {
        DocumentGraph graph = CreateGraph(
            "Paged",
            "document-query-paged");
        LegalDocument[] documents = Enumerable.Range(1, 23)
            .Select(index => CreateDocument(
                graph,
                $"document-{index:D2}.pdf",
                index,
                CreatedAt.AddMinutes(index)))
            .ToArray();
        LegalDocument sameTimeFirst = CreateDocument(
            graph,
            "same-time-a.pdf",
            24,
            CreatedAt.AddHours(2));
        LegalDocument sameTimeSecond = CreateDocument(
            graph,
            "same-time-b.pdf",
            25,
            CreatedAt.AddHours(2));
        LegalDocument[] allDocuments = documents
            .Append(sameTimeFirst)
            .Append(sameTimeSecond)
            .ToArray();
        await SeedAsync(graph.Entities.Concat(allDocuments).ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new LegalDocumentReadQueries(dbContext);

        LegalDocumentListReadPage firstPage = await queries.ListAsync(
            CreateRequest(graph.Organization.Id, pageNumber: 1, pageSize: 20));
        LegalDocumentListReadPage secondPage = await queries.ListAsync(
            CreateRequest(graph.Organization.Id, pageNumber: 2, pageSize: 20));

        Guid[] expected = allDocuments
            .OrderByDescending(document => document.CreatedAt)
            .ThenByDescending(document => document.Id)
            .Select(document => document.Id)
            .ToArray();
        Assert.Equal(expected[..20], firstPage.Items.Select(item => item.Id));
        Assert.Equal(expected[20..], secondPage.Items.Select(item => item.Id));
        Assert.True(firstPage.HasNext);
        Assert.False(secondPage.HasNext);
    }

    [Fact]
    public async Task FindAsync_WithContextualKeys_ReturnsOnlySameTenantMetadata()
    {
        DocumentGraph graphA = CreateGraph(
            "Alpha detail",
            "document-detail-alpha");
        DocumentGraph graphB = CreateGraph(
            "Beta detail",
            "document-detail-beta");
        LegalDocument documentA = CreateDocument(
            graphA,
            "alpha.pdf",
            30);
        LegalDocument documentB = CreateDocument(
            graphB,
            "beta.pdf",
            31);
        await SeedAsync(
            graphA.Entities
                .Concat(graphB.Entities)
                .Concat([documentA, documentB])
                .ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext =
            CreateQueryContext(interceptor);
        var queries = new LegalDocumentReadQueries(dbContext);

        LegalDocumentMetadataReadModel? sameTenant =
            await queries.FindAsync(
                documentA.Id,
                graphA.Organization.Id);
        LegalDocumentMetadataReadModel? foreignTenant =
            await queries.FindAsync(
                documentB.Id,
                graphA.Organization.Id);

        Assert.NotNull(sameTenant);
        Assert.Equal(documentA.Id, sameTenant.Id);
        Assert.Equal(documentA.OriginalFileName, sameTenant.OriginalFileName);
        Assert.Equal(
            documentA.ContentHashSha256,
            sameTenant.ContentHashSha256);
        Assert.Null(foreignTenant);
        Assert.Equal(2, interceptor.CommandTexts.Count);
        Assert.All(
            interceptor.CommandTexts,
            command =>
            {
                Assert.Contains("organization_id", command);
                Assert.Contains("id", command);
                Assert.DoesNotContain(
                    "stored_object_key",
                    command,
                    StringComparison.OrdinalIgnoreCase);
            });
    }

    private static LegalDocumentListReadRequest CreateRequest(
        Guid organizationId,
        string? search = null,
        Guid? processId = null,
        Guid? clientId = null,
        int pageNumber = 1,
        int pageSize = 20)
    {
        return new LegalDocumentListReadRequest(
            organizationId,
            search,
            processId,
            clientId,
            pageNumber,
            pageSize);
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

    private static DocumentGraph CreateGraph(
        string name,
        string slug,
        bool includeRelations = false)
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

        if (!includeRelations)
        {
            return new DocumentGraph(
                organization,
                user,
                membership,
                null,
                null,
                null,
                null);
        }

        var client = new Client(
            organization.Id,
            $"{name} client",
            CreatedAt);
        var otherClient = new Client(
            organization.Id,
            $"{name} other client",
            CreatedAt.AddMinutes(1));
        var process = new LegalProcess(
            organization.Id,
            client.Id,
            $"{name} process",
            CreatedAt);
        var otherProcess = new LegalProcess(
            organization.Id,
            otherClient.Id,
            $"{name} other process",
            CreatedAt.AddMinutes(1));
        return new DocumentGraph(
            organization,
            user,
            membership,
            client,
            otherClient,
            process,
            otherProcess);
    }

    private static LegalDocument CreateDocument(
        DocumentGraph graph,
        string originalFileName,
        int key,
        DateTimeOffset? createdAt = null,
        Guid? clientId = null,
        Guid? processId = null)
    {
        return new LegalDocument(
            graph.Organization.Id,
            clientId,
            processId,
            originalFileName,
            key.ToString("x32", CultureInfo.InvariantCulture),
            "application/pdf",
            100 + key,
            new LegalDocumentContentHash(
                Enumerable.Repeat((byte)key, 32).ToArray()),
            graph.Membership.Id,
            createdAt ?? CreatedAt.AddMinutes(key));
    }

    private sealed record DocumentGraph(
        Organization Organization,
        User User,
        OrganizationMembership Membership,
        Client? Client,
        Client? OtherClient,
        LegalProcess? Process,
        LegalProcess? OtherProcess)
    {
        public object[] Entities =>
            new object?[]
            {
                Organization,
                User,
                Membership,
                Client,
                OtherClient,
                Process,
                OtherProcess
            }
                .Where(entity => entity is not null)
                .Cast<object>()
                .ToArray();
    }

    private sealed class ReaderCommandInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commandTexts = [];

        public IReadOnlyList<string> CommandTexts => _commandTexts;

        public override ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            _commandTexts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
