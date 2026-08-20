using System.Data.Common;
using System.Globalization;
using Enma.Application.Authorization;
using Enma.Application.Documents;
using Enma.Application.Documents.GetById;
using Enma.Application.Documents.List;
using Enma.Domain.Clients;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Application.Documents;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDocumentReadUseCasesPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        20,
        14,
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

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_WithLiveReadRole_AllowsGetAndList(
        OrganizationRole role)
    {
        AccessGraph graph = CreateGraph("Alpha", "document-read-alpha", role);
        LegalDocument document = CreateDocument(graph, "visible.pdf", 1);
        await SeedAsync(graph.Entities.Append(document).ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        (GetLegalDocumentUseCase getUseCase,
            ListLegalDocumentsUseCase listUseCase) = CreateUseCases(dbContext);

        GetLegalDocumentResult get = await getUseCase.ExecuteAsync(
            new GetLegalDocumentQuery(
                graph.User.Id,
                graph.Organization.Id,
                document.Id));
        ListLegalDocumentsResult list = await listUseCase.ExecuteAsync(
            new ListLegalDocumentsQuery(
                graph.User.Id,
                graph.Organization.Id));

        Assert.Equal(GetLegalDocumentResultStatus.Succeeded, get.Status);
        Assert.Equal(document.Id, get.Document?.Id);
        Assert.Equal(ListLegalDocumentsResultStatus.Succeeded, list.Status);
        Assert.Equal([document.Id], list.Items.Select(item => item.Id));
    }

    [Theory]
    [InlineData(AccessDenial.MissingMembership)]
    [InlineData(AccessDenial.InactiveUser)]
    [InlineData(AccessDenial.InactiveMembership)]
    [InlineData(AccessDenial.InactiveOrganization)]
    public async Task ExecuteAsync_WithoutLiveActorState_DeniesBeforeDocumentQuery(
        AccessDenial denial)
    {
        AccessGraph graph = CreateGraph(
            "Denied",
            $"document-read-denied-{denial}",
            OrganizationRole.Owner);

        if (denial == AccessDenial.InactiveUser)
        {
            graph.User.Deactivate();
        }
        else if (denial == AccessDenial.InactiveMembership)
        {
            graph.Membership.Deactivate();
        }
        else if (denial == AccessDenial.InactiveOrganization)
        {
            graph.Organization.Deactivate();
        }

        object[] entities = denial == AccessDenial.MissingMembership
            ? [graph.Organization, graph.User]
            : graph.Entities;
        await SeedAsync(entities);
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext =
            CreateInterceptedContext(interceptor);
        (GetLegalDocumentUseCase getUseCase,
            ListLegalDocumentsUseCase listUseCase) = CreateUseCases(dbContext);

        GetLegalDocumentResult get = await getUseCase.ExecuteAsync(
            new GetLegalDocumentQuery(
                graph.User.Id,
                graph.Organization.Id,
                Guid.NewGuid()));
        ListLegalDocumentsResult list = await listUseCase.ExecuteAsync(
            new ListLegalDocumentsQuery(
                graph.User.Id,
                graph.Organization.Id));

        Assert.Same(GetLegalDocumentResult.AccessDenied, get);
        Assert.Same(ListLegalDocumentsResult.AccessDenied, list);
        Assert.Equal(2, interceptor.CommandTexts.Count);
        Assert.All(
            interceptor.CommandTexts,
            command => Assert.DoesNotContain(
                "legal_documents",
                command,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_MissingAndForeignDocument_ReturnSameNotFound()
    {
        AccessGraph graphA = CreateGraph(
            "Alpha detail",
            "document-read-detail-alpha",
            OrganizationRole.Owner);
        AccessGraph graphB = CreateGraph(
            "Beta detail",
            "document-read-detail-beta",
            OrganizationRole.Owner);
        LegalDocument foreignDocument = CreateDocument(
            graphB,
            "foreign.pdf",
            10);
        await SeedAsync(
            graphA.Entities
                .Concat(graphB.Entities)
                .Append(foreignDocument)
                .ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        (GetLegalDocumentUseCase getUseCase, _) = CreateUseCases(dbContext);

        GetLegalDocumentResult missing = await getUseCase.ExecuteAsync(
            new GetLegalDocumentQuery(
                graphA.User.Id,
                graphA.Organization.Id,
                Guid.NewGuid()));
        GetLegalDocumentResult foreign = await getUseCase.ExecuteAsync(
            new GetLegalDocumentQuery(
                graphA.User.Id,
                graphA.Organization.Id,
                foreignDocument.Id));

        Assert.Same(GetLegalDocumentResult.NotFound, missing);
        Assert.Same(missing, foreign);
    }

    [Fact]
    public async Task ExecuteAsync_ListAndForeignFilters_DoNotCrossTenantBoundary()
    {
        AccessGraph graphA = CreateGraph(
            "Alpha list",
            "document-read-list-alpha",
            OrganizationRole.Member,
            includeRelations: true);
        AccessGraph graphB = CreateGraph(
            "Beta list",
            "document-read-list-beta",
            OrganizationRole.Member,
            includeRelations: true);
        LegalDocument documentA = CreateDocument(
            graphA,
            "alpha.pdf",
            20,
            clientId: graphA.Client!.Id);
        LegalDocument documentB = CreateDocument(
            graphB,
            "beta.pdf",
            21,
            processId: graphB.Process!.Id);
        await SeedAsync(
            graphA.Entities
                .Concat(graphB.Entities)
                .Concat([documentA, documentB])
                .ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        (_, ListLegalDocumentsUseCase listUseCase) = CreateUseCases(dbContext);

        ListLegalDocumentsResult all = await listUseCase.ExecuteAsync(
            new ListLegalDocumentsQuery(
                graphA.User.Id,
                graphA.Organization.Id));
        ListLegalDocumentsResult foreignClient =
            await listUseCase.ExecuteAsync(
                new ListLegalDocumentsQuery(
                    graphA.User.Id,
                    graphA.Organization.Id,
                    ClientId: graphB.Client!.Id));
        ListLegalDocumentsResult foreignProcess =
            await listUseCase.ExecuteAsync(
                new ListLegalDocumentsQuery(
                    graphA.User.Id,
                    graphA.Organization.Id,
                    ProcessId: graphB.Process!.Id));

        Assert.Equal([documentA.Id], all.Items.Select(item => item.Id));
        Assert.Equal(
            ListLegalDocumentsResultStatus.Succeeded,
            foreignClient.Status);
        Assert.Empty(foreignClient.Items);
        Assert.Equal(
            ListLegalDocumentsResultStatus.Succeeded,
            foreignProcess.Status);
        Assert.Empty(foreignProcess.Items);
    }

    private static (GetLegalDocumentUseCase Get, ListLegalDocumentsUseCase List)
        CreateUseCases(EnmaDbContext dbContext)
    {
        var readAuthorization = new LegalDocumentReadAuthorization(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(dbContext)));
        var queries = new LegalDocumentReadQueries(dbContext);
        return (
            new GetLegalDocumentUseCase(readAuthorization, queries),
            new ListLegalDocumentsUseCase(readAuthorization, queries));
    }

    private EnmaDbContext CreateInterceptedContext(
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

    private static AccessGraph CreateGraph(
        string name,
        string slug,
        OrganizationRole role,
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
            role,
            CreatedAt);

        if (!includeRelations)
        {
            return new AccessGraph(
                organization,
                user,
                membership,
                null,
                null);
        }

        var client = new Client(
            organization.Id,
            $"{name} client",
            CreatedAt);
        var process = new LegalProcess(
            organization.Id,
            client.Id,
            $"{name} process",
            CreatedAt);
        return new AccessGraph(
            organization,
            user,
            membership,
            client,
            process);
    }

    private static LegalDocument CreateDocument(
        AccessGraph graph,
        string originalFileName,
        int key,
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
            CreatedAt.AddMinutes(key));
    }

    private sealed record AccessGraph(
        Organization Organization,
        User User,
        OrganizationMembership Membership,
        Client? Client,
        LegalProcess? Process)
    {
        public object[] Entities =>
            new object?[]
            {
                Organization,
                User,
                Membership,
                Client,
                Process
            }
                .Where(entity => entity is not null)
                .Cast<object>()
                .ToArray();
    }

    public enum AccessDenial
    {
        MissingMembership = 0,
        InactiveUser = 1,
        InactiveMembership = 2,
        InactiveOrganization = 3
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
