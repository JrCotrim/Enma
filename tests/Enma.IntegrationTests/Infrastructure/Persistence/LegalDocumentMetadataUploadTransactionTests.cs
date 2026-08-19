using Enma.Application.Documents.Storage;
using Enma.Application.Documents.Upload;
using Enma.Domain.Clients;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDocumentMetadataUploadTransactionTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        19,
        19,
        30,
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
    public async Task ExecuteAsync_WithGeneralDocument_LocksLiveActorAndPersists()
    {
        SeedGraph graph = await SeedGraphAsync();
        LegalDocumentUploadPersistenceRequest request =
            CreateRequest(graph);

        LegalDocumentUploadLockedState? observedState = null;
        var attempt = new LegalDocumentMetadataUploadAttempt();
        LegalDocumentUploadPersistenceResult result =
            await ExecuteTransactionAsync(
                request,
                state =>
                {
                    observedState = state;
                    return LegalDocumentUploadDecision.Persist(
                        CreateDocument(request));
                },
                attempt);

        Assert.True(attempt.CommitStarted);
        Assert.Equal(
            LegalDocumentUploadPersistenceResultStatus.Persisted,
            result.Status);
        Assert.NotNull(result.DocumentId);
        LegalDocumentUploadLockedState lockedState =
            Assert.IsType<LegalDocumentUploadLockedState>(observedState);
        LegalDocumentUploadActorState actor =
            Assert.IsType<LegalDocumentUploadActorState>(lockedState.Actor);

        Assert.Equal(graph.User.Id, actor.UserId);
        Assert.Equal(graph.Organization.Id, actor.OrganizationId);
        Assert.Equal(graph.Membership.Id, actor.MembershipId);
        Assert.Equal(OrganizationRole.Owner, actor.Role);
        Assert.True(actor.IsMembershipActive);
        Assert.True(actor.IsUserActive);
        Assert.True(actor.IsOrganizationActive);
        Assert.Null(lockedState.Client);
        Assert.Null(lockedState.Process);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        LegalDocument persisted = await dbContext.LegalDocuments
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(result.DocumentId, persisted.Id);
        Assert.Equal(request.OrganizationId, persisted.OrganizationId);
        Assert.Equal(
            request.ActorMembershipId,
            persisted.UploadedByMembershipId);
        Assert.Equal(request.ObjectKey.Value, persisted.StoredObjectKey);
    }

    [Fact]
    public async Task ExecuteAsync_WithDirectClient_ExposesLockedActiveClient()
    {
        SeedGraph graph = await SeedGraphAsync(includeClient: true);
        LegalDocumentUploadPersistenceRequest request =
            CreateRequest(graph, clientId: graph.Client!.Id);

        LegalDocumentUploadClientState? observedClient = null;
        LegalDocumentUploadPersistenceResult result =
            await ExecuteTransactionAsync(
                request,
                state =>
                {
                    observedClient = state.Client;
                    return LegalDocumentUploadDecision.Persist(
                        CreateDocument(request));
                });

        Assert.Equal(
            LegalDocumentUploadPersistenceResultStatus.Persisted,
            result.Status);
        LegalDocumentUploadClientState clientState =
            Assert.IsType<LegalDocumentUploadClientState>(observedClient);

        Assert.Equal(graph.Client.Id, clientState.ClientId);
        Assert.Equal(
            graph.Organization.Id,
            clientState.OrganizationId);
        Assert.True(clientState.IsActive);
    }

    [Fact]
    public async Task ExecuteAsync_WithProcess_ExposesLockedSameTenantProcess()
    {
        SeedGraph graph = await SeedGraphAsync(
            includeClient: true,
            includeProcess: true);
        LegalDocumentUploadPersistenceRequest request =
            CreateRequest(graph, processId: graph.Process!.Id);

        LegalDocumentUploadProcessState? observedProcess = null;
        LegalDocumentUploadPersistenceResult result =
            await ExecuteTransactionAsync(
                request,
                state =>
                {
                    observedProcess = state.Process;
                    return LegalDocumentUploadDecision.Persist(
                        CreateDocument(request));
                });

        Assert.Equal(
            LegalDocumentUploadPersistenceResultStatus.Persisted,
            result.Status);
        LegalDocumentUploadProcessState processState =
            Assert.IsType<LegalDocumentUploadProcessState>(observedProcess);

        Assert.Equal(graph.Process.Id, processState.ProcessId);
        Assert.Equal(
            graph.Organization.Id,
            processState.OrganizationId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("cross-tenant")]
    [InlineData("inactive")]
    public async Task ExecuteAsync_WithUnavailableClient_RollsBackWithoutMetadata(
        string condition)
    {
        SeedGraph graph = await SeedGraphAsync(
            includeClient: condition == "inactive",
            clientActive: condition != "inactive");

        Guid clientId;
        SeedGraph? otherGraph = null;

        if (condition == "cross-tenant")
        {
            otherGraph = await SeedGraphAsync(
                organizationName: "Beta",
                organizationSlug: "beta",
                userEmail: "beta@example.com",
                includeClient: true);
            clientId = otherGraph.Client!.Id;
        }
        else if (condition == "inactive")
        {
            clientId = graph.Client!.Id;
        }
        else
        {
            clientId = Guid.NewGuid();
        }

        LegalDocumentUploadPersistenceRequest request =
            CreateRequest(graph, clientId: clientId);

        LegalDocumentUploadClientState? observedClient = null;
        LegalDocumentUploadPersistenceResult result =
            await ExecuteTransactionAsync(
                request,
                state =>
                {
                    observedClient = state.Client;
                    return LegalDocumentUploadDecision.RelatedClientUnavailable;
                });

        Assert.Equal(
            LegalDocumentUploadPersistenceResultStatus.RelatedClientUnavailable,
            result.Status);

        if (condition == "inactive")
        {
            LegalDocumentUploadClientState clientState =
                Assert.IsType<LegalDocumentUploadClientState>(observedClient);
            Assert.False(clientState.IsActive);
        }
        else
        {
            Assert.Null(observedClient);
        }

        await AssertNoDocumentsAsync();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("cross-tenant")]
    public async Task ExecuteAsync_WithUnavailableProcess_RollsBackWithoutMetadata(
        string condition)
    {
        SeedGraph graph = await SeedGraphAsync();

        Guid processId;

        if (condition == "cross-tenant")
        {
            SeedGraph otherGraph = await SeedGraphAsync(
                organizationName: "Beta",
                organizationSlug: "beta",
                userEmail: "beta@example.com",
                includeClient: true,
                includeProcess: true);
            processId = otherGraph.Process!.Id;
        }
        else
        {
            processId = Guid.NewGuid();
        }

        LegalDocumentUploadPersistenceRequest request =
            CreateRequest(graph, processId: processId);

        LegalDocumentUploadProcessState? observedProcess = null;
        LegalDocumentUploadPersistenceResult result =
            await ExecuteTransactionAsync(
                request,
                state =>
                {
                    observedProcess = state.Process;
                    return LegalDocumentUploadDecision.RelatedProcessUnavailable;
                });

        Assert.Equal(
            LegalDocumentUploadPersistenceResultStatus.RelatedProcessUnavailable,
            result.Status);
        Assert.Null(observedProcess);

        await AssertNoDocumentsAsync();
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("user")]
    [InlineData("organization")]
    public async Task ExecuteAsync_WithInactiveActorState_ExposesCurrentStateAndRollsBack(
        string inactivePart)
    {
        SeedGraph graph = await SeedGraphAsync(
            membershipActive: inactivePart != "membership",
            userActive: inactivePart != "user",
            organizationActive: inactivePart != "organization");
        LegalDocumentUploadPersistenceRequest request =
            CreateRequest(graph);

        LegalDocumentUploadActorState? observedActor = null;
        LegalDocumentUploadPersistenceResult result =
            await ExecuteTransactionAsync(
                request,
                state =>
                {
                    observedActor = state.Actor;
                    return LegalDocumentUploadDecision.AccessDenied;
                });

        Assert.Equal(
            LegalDocumentUploadPersistenceResultStatus.AccessDenied,
            result.Status);
        LegalDocumentUploadActorState actorState =
            Assert.IsType<LegalDocumentUploadActorState>(observedActor);
        Assert.Equal(
            inactivePart != "membership",
            actorState.IsMembershipActive);
        Assert.Equal(
            inactivePart != "user",
            actorState.IsUserActive);
        Assert.Equal(
            inactivePart != "organization",
            actorState.IsOrganizationActive);

        await AssertNoDocumentsAsync();
    }

    [Fact]
    public async Task ExecuteAsync_WithCrossTenantActorMembership_DoesNotExposeActor()
    {
        SeedGraph graphA = await SeedGraphAsync();
        SeedGraph graphB = await SeedGraphAsync(
            organizationName: "Beta",
            organizationSlug: "beta",
            userEmail: "beta@example.com");

        LegalDocumentUploadPersistenceRequest request =
            CreateRequest(
                graphA,
                actorMembershipId: graphB.Membership.Id);

        LegalDocumentUploadActorState? observedActor = null;
        LegalDocumentUploadPersistenceResult result =
            await ExecuteTransactionAsync(
                request,
                state =>
                {
                    observedActor = state.Actor;
                    return LegalDocumentUploadDecision.AccessDenied;
                });

        Assert.Equal(
            LegalDocumentUploadPersistenceResultStatus.AccessDenied,
            result.Status);
        Assert.Null(observedActor);

        await AssertNoDocumentsAsync();
    }

    [Fact]
    public async Task ExecuteAsync_UsesCurrentLockedRoleInsteadOfEarlierRole()
    {
        SeedGraph graph = await SeedGraphAsync(
            role: OrganizationRole.Member);
        LegalDocumentUploadPersistenceRequest request =
            CreateRequest(graph);

        OrganizationRole? observedRole = null;
        LegalDocumentUploadPersistenceResult result =
            await ExecuteTransactionAsync(
                request,
                state =>
                {
                    observedRole = state.Actor?.Role;
                    return LegalDocumentUploadDecision.AccessDenied;
                });

        Assert.Equal(OrganizationRole.Member, observedRole);
        Assert.Equal(
            LegalDocumentUploadPersistenceResultStatus.AccessDenied,
            result.Status);

        await AssertNoDocumentsAsync();
    }

    [Fact]
    public async Task ExecuteAsync_WithRejectedDecision_RollsBackWithoutMetadata()
    {
        SeedGraph graph = await SeedGraphAsync();
        LegalDocumentUploadPersistenceRequest request =
            CreateRequest(graph);

        var attempt = new LegalDocumentMetadataUploadAttempt();
        LegalDocumentUploadPersistenceResult result =
            await ExecuteTransactionAsync(
                request,
                _ => LegalDocumentUploadDecision.AccessDenied,
                attempt);

        Assert.False(attempt.CommitStarted);
        Assert.Equal(
            LegalDocumentUploadPersistenceResultStatus.AccessDenied,
            result.Status);

        await AssertNoDocumentsAsync();
    }

    [Fact]
    public async Task ExecuteAsync_WithMismatchedPersistDecision_ThrowsAndRollsBack()
    {
        SeedGraph graph = await SeedGraphAsync();
        LegalDocumentUploadPersistenceRequest request =
            CreateRequest(graph);

        var mismatchedKey = LegalDocumentStorageObjectKey.CreateNew();

        var attempt = new LegalDocumentMetadataUploadAttempt();
        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ExecuteTransactionAsync(
                    request,
                    _ => LegalDocumentUploadDecision.Persist(
                        CreateDocument(
                            request,
                            storedObjectKey: mismatchedKey.Value)),
                    attempt));

        Assert.False(attempt.CommitStarted);
        Assert.Equal(
            "The legal document persistence decision does not match the validated upload request.",
            exception.Message);

        await AssertNoDocumentsAsync();
    }

    private Task<LegalDocumentUploadPersistenceResult> ExecuteTransactionAsync(
        LegalDocumentUploadPersistenceRequest request,
        Func<LegalDocumentUploadLockedState, LegalDocumentUploadDecision> decide,
        LegalDocumentMetadataUploadAttempt? attempt = null,
        CancellationToken cancellationToken = default)
    {
        return CreateTransaction().ExecuteAsync(
            request,
            decide,
            attempt ?? new LegalDocumentMetadataUploadAttempt(),
            cancellationToken);
    }

    private LegalDocumentMetadataUploadTransaction CreateTransaction()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;

        return new LegalDocumentMetadataUploadTransaction(options);
    }

    private async Task<SeedGraph> SeedGraphAsync(
        string organizationName = "Alpha",
        string organizationSlug = "alpha",
        string userEmail = "alpha@example.com",
        OrganizationRole role = OrganizationRole.Owner,
        bool organizationActive = true,
        bool membershipActive = true,
        bool userActive = true,
        bool includeClient = false,
        bool clientActive = true,
        bool includeProcess = false)
    {
        var organization = new Organization(
            organizationName,
            organizationSlug,
            CreatedAt);
        var user = new User(
            $"{organizationName} User",
            userEmail,
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            CreatedAt);

        if (!organizationActive)
        {
            organization.Deactivate();
        }

        if (!userActive)
        {
            user.Deactivate();
        }

        if (!membershipActive)
        {
            membership.Deactivate();
        }

        Client? client = null;
        LegalProcess? process = null;

        if (includeClient || includeProcess)
        {
            client = new Client(
                organization.Id,
                $"{organizationName} Client",
                CreatedAt);

            if (!clientActive)
            {
                client.Deactivate();
            }
        }

        if (includeProcess)
        {
            process = new LegalProcess(
                organization.Id,
                client!.Id,
                $"{organizationName} Process",
                CreatedAt);
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        dbContext.Organizations.Add(organization);
        dbContext.Users.Add(user);
        dbContext.OrganizationMemberships.Add(membership);

        if (client is not null)
        {
            dbContext.Clients.Add(client);
        }

        if (process is not null)
        {
            dbContext.LegalProcesses.Add(process);
        }

        await dbContext.SaveChangesAsync();

        return new SeedGraph(
            organization,
            user,
            membership,
            client,
            process);
    }

    private static LegalDocumentUploadPersistenceRequest CreateRequest(
        SeedGraph graph,
        Guid? actorMembershipId = null,
        Guid? clientId = null,
        Guid? processId = null)
    {
        return new LegalDocumentUploadPersistenceRequest(
            graph.User.Id,
            graph.Organization.Id,
            actorMembershipId ?? graph.Membership.Id,
            clientId,
            processId,
            "contract.pdf",
            LegalDocumentStorageObjectKey.CreateNew(),
            "application/pdf",
            128,
            CreateHash());
    }

    private static LegalDocument CreateDocument(
        LegalDocumentUploadPersistenceRequest request,
        string? storedObjectKey = null)
    {
        return new LegalDocument(
            request.OrganizationId,
            request.ClientId,
            request.ProcessId,
            request.OriginalFileName,
            storedObjectKey ?? request.ObjectKey.Value,
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

    private async Task AssertNoDocumentsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(
            0,
            await dbContext.LegalDocuments.CountAsync());
    }

    private sealed record SeedGraph(
        Organization Organization,
        User User,
        OrganizationMembership Membership,
        Client? Client,
        LegalProcess? Process);
}
