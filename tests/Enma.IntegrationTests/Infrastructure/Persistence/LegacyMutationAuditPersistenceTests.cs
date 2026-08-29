using Enma.Application.Clients;
using Enma.Application.Deadlines;
using Enma.Application.Processes;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegacyMutationAuditPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset SeededAt = new(
        2026, 8, 14, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OccurredAt = SeededAt.AddHours(1);
    private static readonly DateOnly InitialDueDate = new(2026, 9, 1);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ClientOperations_RecordActorAndFourEvents_WithoutNoOpAudits()
    {
        LegacyGraph graph = await SeedGraphAsync();
        DbContextOptions<EnmaDbContext> options = CreateOptions();
        var creation = new ClientCreationPersistence(options, Clock());
        var mutation = new ClientMutationPersistence(options, Clock());
        var createRequest = new ClientCreationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id);

        ClientCreationPersistenceResult created = await creation.ExecuteAsync(
            createRequest,
            state => IsAuthorized(createRequest, state)
                ? ClientCreationDecision.Persist(new Client(
                    graph.Organization.Id,
                    "Created client",
                    OccurredAt))
                : ClientCreationDecision.AccessDenied);
        Guid clientId = Assert.IsType<Guid>(created.ClientId);
        var request = new ClientMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            clientId);

        await mutation.UpdateNameAsync(
            request,
            state => Rename(state, "  Renamed client  "));
        await mutation.UpdateNameAsync(
            request,
            state => Rename(state, " Renamed client "));
        await mutation.DeactivateAsync(request, Deactivate);
        await mutation.DeactivateAsync(request, Deactivate);
        await mutation.ReactivateAsync(request, Reactivate);
        await mutation.ReactivateAsync(request, Reactivate);

        AuditLog[] auditLogs = await FindAuditLogsAsync();
        Assert.Equal(4, auditLogs.Length);
        AssertEventSet(
            auditLogs,
            clientId,
            AuditEntityType.Client,
            graph,
            AuditEventType.ClientCreated,
            AuditEventType.ClientRenamed,
            AuditEventType.ClientDeactivated,
            AuditEventType.ClientReactivated);
        Assert.All(auditLogs, auditLog => Assert.Null(auditLog.Details));

        Client persisted = await FindClientAsync(clientId);
        Assert.Equal("Renamed client", persisted.Name);
        Assert.True(persisted.IsActive);
    }

    [Fact]
    public async Task LegalProcessOperations_RecordActorAndTwoNullDetailsEvents_WithoutNoOpAudit()
    {
        LegacyGraph graph = await SeedGraphAsync();
        DbContextOptions<EnmaDbContext> options = CreateOptions();
        var creation = new LegalProcessCreationPersistence(options, Clock());
        var mutation = new LegalProcessMutationPersistence(options, Clock());
        var createRequest = new LegalProcessCreationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.Client.Id);

        LegalProcessCreationPersistenceResult created = await creation.ExecuteAsync(
            createRequest,
            state => IsAuthorized(createRequest, state) && state.IsClientAvailable
                ? LegalProcessCreationDecision.Persist(new LegalProcess(
                    graph.Organization.Id,
                    graph.Client.Id,
                    "Created process",
                    OccurredAt))
                : LegalProcessCreationDecision.AccessDenied);
        Guid processId = Assert.IsType<Guid>(created.ProcessId);
        var request = new LegalProcessMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            processId);

        await mutation.UpdateTitleAsync(
            request,
            state => ChangeTitle(state, "  Renamed process  "));
        await mutation.UpdateTitleAsync(
            request,
            state => ChangeTitle(state, " Renamed process "));

        AuditLog[] auditLogs = await FindAuditLogsAsync();
        Assert.Equal(2, auditLogs.Length);
        AssertEventSet(
            auditLogs,
            processId,
            AuditEntityType.LegalProcess,
            graph,
            AuditEventType.LegalProcessCreated,
            AuditEventType.LegalProcessTitleChanged);
        Assert.All(auditLogs, auditLog => Assert.Null(auditLog.Details));
        Assert.Equal("Renamed process", (await FindProcessAsync(processId)).Title);
    }

    [Fact]
    public async Task LegalDeadlineOperations_RecordExactFieldsAndLifecycleEvents_WithoutNoOpAudits()
    {
        LegacyGraph graph = await SeedGraphAsync();
        DbContextOptions<EnmaDbContext> options = CreateOptions();
        var creation = new LegalDeadlineCreationPersistence(options, Clock());
        var mutation = new LegalDeadlineMutationPersistence(options, Clock());
        var createRequest = new LegalDeadlineCreationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.Process.Id);

        LegalDeadlineCreationPersistenceResult created = await creation.ExecuteAsync(
            createRequest,
            state => IsAuthorized(createRequest, state) && state.IsProcessAvailable
                ? LegalDeadlineCreationDecision.Persist(new LegalDeadline(
                    graph.Organization.Id,
                    graph.Process.Id,
                    "Created deadline",
                    InitialDueDate,
                    OccurredAt))
                : LegalDeadlineCreationDecision.AccessDenied);
        Guid deadlineId = Assert.IsType<Guid>(created.DeadlineId);
        var request = new LegalDeadlineMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            deadlineId);
        DateOnly changedDueDate = InitialDueDate.AddDays(1);

        await mutation.UpdateDetailsAsync(
            request,
            state => ChangeDetails(
                state,
                "  Changed deadline  ",
                changedDueDate));
        await mutation.UpdateDetailsAsync(
            request,
            state => ChangeDetails(
                state,
                " Changed deadline ",
                changedDueDate));
        await mutation.CompleteAsync(
            request,
            state => Complete(state, OccurredAt.AddHours(1)));
        await mutation.CompleteAsync(
            request,
            state => Complete(state, OccurredAt.AddHours(2)));
        await mutation.ReopenAsync(request, Reopen);
        await mutation.ReopenAsync(request, Reopen);

        AuditLog[] auditLogs = await FindAuditLogsAsync();
        Assert.Equal(4, auditLogs.Length);
        AssertEventSet(
            auditLogs,
            deadlineId,
            AuditEntityType.LegalDeadline,
            graph,
            AuditEventType.LegalDeadlineCreated,
            AuditEventType.LegalDeadlineDetailsChanged,
            AuditEventType.LegalDeadlineCompleted,
            AuditEventType.LegalDeadlineReopened);

        AuditLog detailsLog = Assert.Single(
            auditLogs,
            auditLog => auditLog.EventType ==
                AuditEventType.LegalDeadlineDetailsChanged);
        LegalDeadlineDetailsChangedAuditDetails details =
            Assert.IsType<LegalDeadlineDetailsChangedAuditDetails>(
                detailsLog.Details);
        Assert.Equal(
            [LegalDeadlineChangedField.Title, LegalDeadlineChangedField.DueDate],
            details.ChangedFields);
        Assert.All(
            auditLogs.Where(auditLog => auditLog != detailsLog),
            auditLog => Assert.Null(auditLog.Details));

        LegalDeadline persisted = await FindDeadlineAsync(deadlineId);
        Assert.Equal("Changed deadline", persisted.Title);
        Assert.Equal(changedDueDate, persisted.DueDate);
        Assert.Null(persisted.CompletedAt);
    }

    [Fact]
    public async Task LegalDeadlineDetailsChanged_RecordsOnlyEffectiveFieldNames()
    {
        LegacyGraph graph = await SeedGraphAsync();
        var persistence = new LegalDeadlineMutationPersistence(
            CreateOptions(),
            Clock());
        var request = new LegalDeadlineMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.Deadline.Id);
        DateOnly changedDueDate = InitialDueDate.AddDays(1);

        await persistence.UpdateDetailsAsync(
            request,
            state => ChangeDetails(
                state,
                "Changed title",
                InitialDueDate));
        await persistence.UpdateDetailsAsync(
            request,
            state => ChangeDetails(
                state,
                "Changed title",
                changedDueDate));
        await persistence.UpdateDetailsAsync(
            request,
            state => ChangeDetails(
                state,
                "Changed both",
                InitialDueDate));

        AuditLog[] auditLogs = await FindAuditLogsAsync();
        Assert.Equal(3, auditLogs.Length);
        Assert.All(auditLogs, auditLog =>
        {
            Assert.Equal(
                AuditEventType.LegalDeadlineDetailsChanged,
                auditLog.EventType);
            Assert.Equal(graph.Deadline.Id, auditLog.EntityId);
        });
        IReadOnlyList<LegalDeadlineChangedField>[] changedFields = auditLogs
            .Select(auditLog =>
                Assert.IsType<LegalDeadlineDetailsChangedAuditDetails>(
                    auditLog.Details).ChangedFields)
            .ToArray();
        Assert.Contains(
            changedFields,
            fields => fields.SequenceEqual([LegalDeadlineChangedField.Title]));
        Assert.Contains(
            changedFields,
            fields => fields.SequenceEqual([LegalDeadlineChangedField.DueDate]));
        Assert.Contains(
            changedFields,
            fields => fields.SequenceEqual(
                [
                    LegalDeadlineChangedField.Title,
                    LegalDeadlineChangedField.DueDate
                ]));
    }

    [Fact]
    public async Task ClientCreation_RoleDowngradedAfterActorCapture_DeniesWithoutAudit()
    {
        LegacyGraph graph = await SeedGraphAsync();
        var request = new ClientCreationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id);
        await ChangeActorRoleAsync(
            graph.ActorMembership.Id,
            OrganizationRole.Member);

        ClientCreationPersistenceResult result =
            await new ClientCreationPersistence(CreateOptions(), Clock())
                .ExecuteAsync(
                    request,
                    state => IsAuthorized(request, state)
                        ? ClientCreationDecision.Persist(new Client(
                            graph.Organization.Id,
                            "Must not persist",
                            OccurredAt))
                        : ClientCreationDecision.AccessDenied);

        Assert.Equal(ClientCreationDecisionStatus.AccessDenied, result.Status);
        Assert.Equal(1, await CountClientsAsync());
        Assert.Empty(await FindAuditLogsAsync());
    }

    [Fact]
    public async Task ClientMutation_MembershipDeactivatedAfterActorCapture_DeniesWithoutMutationOrAudit()
    {
        LegacyGraph graph = await SeedGraphAsync();
        var request = new ClientMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.Client.Id);
        await DeactivateMembershipAsync(graph.ActorMembership.Id);

        ClientMutationPersistenceResult result =
            await new ClientMutationPersistence(CreateOptions(), Clock())
                .UpdateNameAsync(
                    request,
                    state =>
                    {
                        if (!IsAuthorized(request, state))
                        {
                            return ClientMutationDecision.AccessDenied;
                        }

                        state.Client.ChangeName("Must not persist");
                        return ClientMutationDecision.Persist;
                    });

        Assert.Equal(ClientMutationPersistenceResult.AccessDenied, result);
        Assert.Equal("Initial client", (await FindClientAsync(graph.Client.Id)).Name);
        Assert.Empty(await FindAuditLogsAsync());
    }

    [Fact]
    public async Task LegalProcessCreation_UserDeactivatedAfterActorCapture_DeniesWithoutAudit()
    {
        LegacyGraph graph = await SeedGraphAsync();
        var request = new LegalProcessCreationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.Client.Id);
        await DeactivateUserAsync(graph.ActorUser.Id);

        LegalProcessCreationPersistenceResult result =
            await new LegalProcessCreationPersistence(CreateOptions(), Clock())
                .ExecuteAsync(
                    request,
                    state => IsAuthorized(request, state)
                        ? LegalProcessCreationDecision.Persist(new LegalProcess(
                            graph.Organization.Id,
                            graph.Client.Id,
                            "Must not persist",
                            OccurredAt))
                        : LegalProcessCreationDecision.AccessDenied);

        Assert.Equal(LegalProcessCreationDecisionStatus.AccessDenied, result.Status);
        Assert.Equal(1, await CountProcessesAsync());
        Assert.Empty(await FindAuditLogsAsync());
    }

    [Fact]
    public async Task LegalProcessMutation_OrganizationDeactivatedAfterActorCapture_DeniesWithoutMutationOrAudit()
    {
        LegacyGraph graph = await SeedGraphAsync();
        var request = new LegalProcessMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.Process.Id);
        await DeactivateOrganizationAsync(graph.Organization.Id);

        LegalProcessMutationPersistenceResult result =
            await new LegalProcessMutationPersistence(CreateOptions(), Clock())
                .UpdateTitleAsync(
                    request,
                    state =>
                    {
                        if (!IsAuthorized(request, state))
                        {
                            return LegalProcessMutationDecision.AccessDenied;
                        }

                        state.LegalProcess.ChangeTitle("Must not persist");
                        return LegalProcessMutationDecision.Persist;
                    });

        Assert.Equal(LegalProcessMutationPersistenceResult.AccessDenied, result);
        Assert.Equal(
            "Initial process",
            (await FindProcessAsync(graph.Process.Id)).Title);
        Assert.Empty(await FindAuditLogsAsync());
    }

    [Fact]
    public async Task LegalDeadlineCreation_RoleDowngradedAfterActorCapture_DeniesWithoutAudit()
    {
        LegacyGraph graph = await SeedGraphAsync();
        var request = new LegalDeadlineCreationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.Process.Id);
        await ChangeActorRoleAsync(
            graph.ActorMembership.Id,
            OrganizationRole.Member);

        LegalDeadlineCreationPersistenceResult result =
            await new LegalDeadlineCreationPersistence(CreateOptions(), Clock())
                .ExecuteAsync(
                    request,
                    state => IsAuthorized(request, state)
                        ? LegalDeadlineCreationDecision.Persist(new LegalDeadline(
                            graph.Organization.Id,
                            graph.Process.Id,
                            "Must not persist",
                            InitialDueDate,
                            OccurredAt))
                        : LegalDeadlineCreationDecision.AccessDenied);

        Assert.Equal(LegalDeadlineCreationDecisionStatus.AccessDenied, result.Status);
        Assert.Equal(1, await CountDeadlinesAsync());
        Assert.Empty(await FindAuditLogsAsync());
    }

    [Fact]
    public async Task LegalDeadlineMutation_UserDeactivatedAfterActorCapture_DeniesWithoutMutationOrAudit()
    {
        LegacyGraph graph = await SeedGraphAsync();
        var request = new LegalDeadlineMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.Deadline.Id);
        await DeactivateUserAsync(graph.ActorUser.Id);

        LegalDeadlineLifecycleMutationPersistenceResult result =
            await new LegalDeadlineMutationPersistence(CreateOptions(), Clock())
                .CompleteAsync(
                    request,
                    state =>
                    {
                        if (!IsAuthorized(request, state))
                        {
                            return LegalDeadlineMutationDecision.AccessDenied;
                        }

                        state.LegalDeadline.Complete(OccurredAt);
                        return LegalDeadlineMutationDecision.Persist;
                    });

        Assert.Equal(
            LegalDeadlineLifecycleMutationPersistenceResult.AccessDenied,
            result);
        Assert.Null((await FindDeadlineAsync(graph.Deadline.Id)).CompletedAt);
        Assert.Empty(await FindAuditLogsAsync());
    }

    [Theory]
    [InlineData(AuditFailureOperation.ClientCreate)]
    [InlineData(AuditFailureOperation.ClientRename)]
    [InlineData(AuditFailureOperation.ClientDeactivate)]
    [InlineData(AuditFailureOperation.ClientReactivate)]
    [InlineData(AuditFailureOperation.ProcessCreate)]
    [InlineData(AuditFailureOperation.ProcessTitle)]
    [InlineData(AuditFailureOperation.DeadlineCreate)]
    [InlineData(AuditFailureOperation.DeadlineDetails)]
    [InlineData(AuditFailureOperation.DeadlineComplete)]
    [InlineData(AuditFailureOperation.DeadlineReopen)]
    public async Task AuditInsertFailure_RollsBackEachLegacyBusinessMutation(
        AuditFailureOperation operation)
    {
        LegacyGraph graph = await SeedGraphAsync(
            clientInactive: operation == AuditFailureOperation.ClientReactivate,
            deadlineCompleted: operation == AuditFailureOperation.DeadlineReopen);
        string? invalidDetails = operation == AuditFailureOperation.DeadlineDetails
            ? null
            : "{}";
        DbContextOptions<EnmaDbContext> options = CreateOptions(
            new InvalidAuditDetailsInterceptor(invalidDetails));

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => ExecuteAuditFailureAsync(operation, graph, options));

        PostgresException postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal(
            "ck_audit_logs_details_contract",
            postgresException.ConstraintName);
        Assert.Empty(await FindAuditLogsAsync());
        Assert.Equal(1, await CountClientsAsync());
        Assert.Equal(1, await CountProcessesAsync());
        Assert.Equal(1, await CountDeadlinesAsync());

        Client persistedClient = await FindClientAsync(graph.Client.Id);
        Assert.Equal("Initial client", persistedClient.Name);
        Assert.Equal(
            operation != AuditFailureOperation.ClientReactivate,
            persistedClient.IsActive);
        Assert.Equal(
            "Initial process",
            (await FindProcessAsync(graph.Process.Id)).Title);
        LegalDeadline persistedDeadline = await FindDeadlineAsync(
            graph.Deadline.Id);
        Assert.Equal("Initial deadline", persistedDeadline.Title);
        Assert.Equal(InitialDueDate, persistedDeadline.DueDate);
        Assert.Equal(
            operation == AuditFailureOperation.DeadlineReopen
                ? SeededAt.AddMinutes(30)
                : null,
            persistedDeadline.CompletedAt);
    }

    [Fact]
    public async Task DeniedAndForeignTenantOperations_CreateNoMutationOrAudit()
    {
        LegacyGraph graph = await SeedGraphAsync();
        var otherOrganization = new Organization(
            "Other audit organization",
            "other-audit-organization",
            SeededAt);
        var otherUser = new User(
            "Other audit actor",
            "other-audit-actor@example.test",
            SeededAt);
        var otherMembership = new OrganizationMembership(
            otherOrganization.Id,
            otherUser.Id,
            OrganizationRole.Owner,
            SeededAt);
        await SeedAsync(otherOrganization, otherUser, otherMembership);
        DbContextOptions<EnmaDbContext> options = CreateOptions();

        ClientCreationPersistenceResult deniedCreate =
            await new ClientCreationPersistence(options, Clock()).ExecuteAsync(
                new ClientCreationPersistenceRequest(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    graph.ActorMembership.Id),
                _ => ClientCreationDecision.AccessDenied);
        LegalProcessCreationPersistenceResult foreignProcessCreate =
            await new LegalProcessCreationPersistence(options, Clock())
                .ExecuteAsync(
                    new LegalProcessCreationPersistenceRequest(
                        otherUser.Id,
                        otherOrganization.Id,
                        otherMembership.Id,
                        graph.Client.Id),
                    state => state.IsClientAvailable
                        ? LegalProcessCreationDecision.Persist(new LegalProcess(
                            otherOrganization.Id,
                            graph.Client.Id,
                            "Must not persist",
                            OccurredAt))
                        : LegalProcessCreationDecision.RelatedClientUnavailable);
        LegalDeadlineCreationPersistenceResult foreignDeadlineCreate =
            await new LegalDeadlineCreationPersistence(options, Clock())
                .ExecuteAsync(
                    new LegalDeadlineCreationPersistenceRequest(
                        otherUser.Id,
                        otherOrganization.Id,
                        otherMembership.Id,
                        graph.Process.Id),
                    state => state.IsProcessAvailable
                        ? LegalDeadlineCreationDecision.Persist(new LegalDeadline(
                            otherOrganization.Id,
                            graph.Process.Id,
                            "Must not persist",
                            InitialDueDate,
                            OccurredAt))
                        : LegalDeadlineCreationDecision.RelatedProcessUnavailable);

        var foreignClientRequest = new ClientMutationPersistenceRequest(
            otherUser.Id,
            otherOrganization.Id,
            otherMembership.Id,
            graph.Client.Id);
        ClientMutationPersistenceResult foreignClientMutation =
            await new ClientMutationPersistence(options, Clock()).UpdateNameAsync(
                foreignClientRequest,
                state => Rename(state, "Must not persist"));
        LegalProcessMutationPersistenceResult foreignProcessMutation =
            await new LegalProcessMutationPersistence(options, Clock())
                .UpdateTitleAsync(
                    new LegalProcessMutationPersistenceRequest(
                        otherUser.Id,
                        otherOrganization.Id,
                        otherMembership.Id,
                        graph.Process.Id),
                    state => ChangeTitle(state, "Must not persist"));
        LegalDeadlineDetailsMutationPersistenceResult foreignDeadlineMutation =
            await new LegalDeadlineMutationPersistence(options, Clock())
                .UpdateDetailsAsync(
                    new LegalDeadlineMutationPersistenceRequest(
                        otherUser.Id,
                        otherOrganization.Id,
                        otherMembership.Id,
                        graph.Deadline.Id),
                    state => ChangeDetails(
                        state,
                        "Must not persist",
                        InitialDueDate.AddDays(1)));

        Assert.Equal(ClientCreationDecisionStatus.AccessDenied, deniedCreate.Status);
        Assert.Equal(
            LegalProcessCreationDecisionStatus.RelatedClientUnavailable,
            foreignProcessCreate.Status);
        Assert.Equal(
            LegalDeadlineCreationDecisionStatus.RelatedProcessUnavailable,
            foreignDeadlineCreate.Status);
        Assert.Equal(ClientMutationPersistenceResult.NotFound, foreignClientMutation);
        Assert.Equal(
            LegalProcessMutationPersistenceResult.NotFound,
            foreignProcessMutation);
        Assert.Equal(
            LegalDeadlineDetailsMutationPersistenceResult.NotFound,
            foreignDeadlineMutation);
        Assert.Equal("Initial client", (await FindClientAsync(graph.Client.Id)).Name);
        Assert.Equal(
            "Initial process",
            (await FindProcessAsync(graph.Process.Id)).Title);
        Assert.Equal(
            "Initial deadline",
            (await FindDeadlineAsync(graph.Deadline.Id)).Title);
        Assert.Empty(await FindAuditLogsAsync());
    }

    [Fact]
    public async Task DomainRejections_RollBackLegacyMutationsWithoutAudit()
    {
        LegacyGraph graph = await SeedGraphAsync();
        DbContextOptions<EnmaDbContext> options = CreateOptions();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new ClientMutationPersistence(options, Clock()).UpdateNameAsync(
                CreateClientMutationRequest(graph),
                state => Rename(state, "   ")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new LegalProcessMutationPersistence(options, Clock()).UpdateTitleAsync(
                new LegalProcessMutationPersistenceRequest(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    graph.ActorMembership.Id,
                    graph.Process.Id),
                state => ChangeTitle(state, "   ")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new LegalDeadlineMutationPersistence(options, Clock())
                .UpdateDetailsAsync(
                    CreateDeadlineMutationRequest(graph),
                    state => ChangeDetails(
                        state,
                        "Valid title",
                        DateOnly.MinValue)));

        Assert.Equal("Initial client", (await FindClientAsync(graph.Client.Id)).Name);
        Assert.Equal(
            "Initial process",
            (await FindProcessAsync(graph.Process.Id)).Title);
        LegalDeadline deadline = await FindDeadlineAsync(graph.Deadline.Id);
        Assert.Equal("Initial deadline", deadline.Title);
        Assert.Equal(InitialDueDate, deadline.DueDate);
        Assert.Empty(await FindAuditLogsAsync());
    }

    private static async Task ExecuteAuditFailureAsync(
        AuditFailureOperation operation,
        LegacyGraph graph,
        DbContextOptions<EnmaDbContext> options)
    {
        switch (operation)
        {
            case AuditFailureOperation.ClientCreate:
                await new ClientCreationPersistence(options, Clock()).ExecuteAsync(
                    new ClientCreationPersistenceRequest(
                        graph.ActorUser.Id,
                        graph.Organization.Id,
                        graph.ActorMembership.Id),
                    _ => ClientCreationDecision.Persist(new Client(
                        graph.Organization.Id,
                        "Must roll back",
                        OccurredAt)));
                break;
            case AuditFailureOperation.ClientRename:
                await CreateClientMutationPersistence(options)
                    .UpdateNameAsync(
                        CreateClientMutationRequest(graph),
                        state => Rename(state, "Must roll back"));
                break;
            case AuditFailureOperation.ClientDeactivate:
                await CreateClientMutationPersistence(options)
                    .DeactivateAsync(
                        CreateClientMutationRequest(graph),
                        Deactivate);
                break;
            case AuditFailureOperation.ClientReactivate:
                await CreateClientMutationPersistence(options)
                    .ReactivateAsync(
                        CreateClientMutationRequest(graph),
                        Reactivate);
                break;
            case AuditFailureOperation.ProcessCreate:
                await new LegalProcessCreationPersistence(options, Clock())
                    .ExecuteAsync(
                        new LegalProcessCreationPersistenceRequest(
                            graph.ActorUser.Id,
                            graph.Organization.Id,
                            graph.ActorMembership.Id,
                            graph.Client.Id),
                        _ => LegalProcessCreationDecision.Persist(new LegalProcess(
                            graph.Organization.Id,
                            graph.Client.Id,
                            "Must roll back",
                            OccurredAt)));
                break;
            case AuditFailureOperation.ProcessTitle:
                await new LegalProcessMutationPersistence(options, Clock())
                    .UpdateTitleAsync(
                        new LegalProcessMutationPersistenceRequest(
                            graph.ActorUser.Id,
                            graph.Organization.Id,
                            graph.ActorMembership.Id,
                            graph.Process.Id),
                        state => ChangeTitle(state, "Must roll back"));
                break;
            case AuditFailureOperation.DeadlineCreate:
                await new LegalDeadlineCreationPersistence(options, Clock())
                    .ExecuteAsync(
                        new LegalDeadlineCreationPersistenceRequest(
                            graph.ActorUser.Id,
                            graph.Organization.Id,
                            graph.ActorMembership.Id,
                            graph.Process.Id),
                        _ => LegalDeadlineCreationDecision.Persist(
                            new LegalDeadline(
                                graph.Organization.Id,
                                graph.Process.Id,
                                "Must roll back",
                                InitialDueDate,
                                OccurredAt)));
                break;
            case AuditFailureOperation.DeadlineDetails:
                await CreateDeadlineMutationPersistence(options)
                    .UpdateDetailsAsync(
                        CreateDeadlineMutationRequest(graph),
                        state => ChangeDetails(
                            state,
                            "Must roll back",
                            InitialDueDate.AddDays(1)));
                break;
            case AuditFailureOperation.DeadlineComplete:
                await CreateDeadlineMutationPersistence(options)
                    .CompleteAsync(
                        CreateDeadlineMutationRequest(graph),
                        state => Complete(state, OccurredAt));
                break;
            case AuditFailureOperation.DeadlineReopen:
                await CreateDeadlineMutationPersistence(options)
                    .ReopenAsync(
                        CreateDeadlineMutationRequest(graph),
                        Reopen);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static ClientMutationPersistence CreateClientMutationPersistence(
        DbContextOptions<EnmaDbContext> options)
    {
        return new ClientMutationPersistence(options, Clock());
    }

    private static ClientMutationPersistenceRequest CreateClientMutationRequest(
        LegacyGraph graph)
    {
        return new ClientMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.Client.Id);
    }

    private static LegalDeadlineMutationPersistence
        CreateDeadlineMutationPersistence(DbContextOptions<EnmaDbContext> options)
    {
        return new LegalDeadlineMutationPersistence(options, Clock());
    }

    private static LegalDeadlineMutationPersistenceRequest
        CreateDeadlineMutationRequest(LegacyGraph graph)
    {
        return new LegalDeadlineMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.Deadline.Id);
    }

    private static ClientMutationDecision Rename(
        ClientMutationLockedState state,
        string name)
    {
        state.Client.ChangeName(name);
        return ClientMutationDecision.Persist;
    }

    private static ClientMutationDecision Deactivate(ClientMutationLockedState state)
    {
        state.Client.Deactivate();
        return ClientMutationDecision.Persist;
    }

    private static ClientMutationDecision Reactivate(ClientMutationLockedState state)
    {
        state.Client.Activate();
        return ClientMutationDecision.Persist;
    }

    private static LegalProcessMutationDecision ChangeTitle(
        LegalProcessMutationLockedState state,
        string title)
    {
        state.LegalProcess.ChangeTitle(title);
        return LegalProcessMutationDecision.Persist;
    }

    private static LegalDeadlineMutationDecision ChangeDetails(
        LegalDeadlineMutationLockedState state,
        string title,
        DateOnly dueDate)
    {
        if (state.LegalDeadline.CompletedAt is not null)
        {
            return LegalDeadlineMutationDecision.Conflict;
        }

        state.LegalDeadline.ChangeDetails(title, dueDate);
        return LegalDeadlineMutationDecision.Persist;
    }

    private static LegalDeadlineMutationDecision Complete(
        LegalDeadlineMutationLockedState state,
        DateTimeOffset completedAt)
    {
        state.LegalDeadline.Complete(completedAt);
        return LegalDeadlineMutationDecision.Persist;
    }

    private static LegalDeadlineMutationDecision Reopen(
        LegalDeadlineMutationLockedState state)
    {
        state.LegalDeadline.Reopen();
        return LegalDeadlineMutationDecision.Persist;
    }

    private static bool IsAuthorized(
        ClientCreationPersistenceRequest request,
        ClientCreationLockedState state)
    {
        return state.IsOrganizationActive &&
            state.Actor is { } actor &&
            actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) &&
            actor.Role is OrganizationRole.Owner or OrganizationRole.Administrator;
    }

    private static bool IsAuthorized(
        LegalProcessCreationPersistenceRequest request,
        LegalProcessCreationLockedState state)
    {
        return state.IsOrganizationActive &&
            state.Actor is { } actor &&
            actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) &&
            actor.Role is OrganizationRole.Owner or OrganizationRole.Administrator;
    }

    private static bool IsAuthorized(
        LegalDeadlineCreationPersistenceRequest request,
        LegalDeadlineCreationLockedState state)
    {
        return state.IsOrganizationActive &&
            state.Actor is { } actor &&
            actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) &&
            actor.Role is OrganizationRole.Owner or OrganizationRole.Administrator;
    }

    private static bool IsAuthorized(
        ClientMutationPersistenceRequest request,
        ClientMutationLockedState state)
    {
        return state.IsOrganizationActive &&
            state.Actor is { } actor &&
            actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) &&
            actor.Role is OrganizationRole.Owner or OrganizationRole.Administrator;
    }

    private static bool IsAuthorized(
        LegalProcessMutationPersistenceRequest request,
        LegalProcessMutationLockedState state)
    {
        return state.IsOrganizationActive &&
            state.Actor is { } actor &&
            actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) &&
            actor.Role is OrganizationRole.Owner or OrganizationRole.Administrator;
    }

    private static bool IsAuthorized(
        LegalDeadlineMutationPersistenceRequest request,
        LegalDeadlineMutationLockedState state)
    {
        return state.IsOrganizationActive &&
            state.Actor is { } actor &&
            actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) &&
            actor.Role is OrganizationRole.Owner or OrganizationRole.Administrator;
    }

    private static void AssertEventSet(
        IEnumerable<AuditLog> auditLogs,
        Guid entityId,
        AuditEntityType entityType,
        LegacyGraph graph,
        params AuditEventType[] eventTypes)
    {
        AuditLog[] logs = auditLogs.ToArray();
        Assert.Equal(
            eventTypes.OrderBy(eventType => eventType),
            logs.Select(auditLog => auditLog.EventType).OrderBy(eventType => eventType));
        Assert.All(logs, auditLog =>
        {
            Assert.Equal(graph.Organization.Id, auditLog.OrganizationId);
            Assert.Equal(graph.ActorUser.Id, auditLog.ActorUserId);
            Assert.Equal(graph.ActorMembership.Id, auditLog.ActorMembershipId);
            Assert.Equal(
                OrganizationRole.Owner,
                auditLog.ActorRoleAtOccurrence);
            Assert.Equal(entityType, auditLog.EntityType);
            Assert.Equal(entityId, auditLog.EntityId);
            Assert.Equal(OccurredAt, auditLog.OccurredAt);
        });
    }

    private DbContextOptions<EnmaDbContext> CreateOptions(
        IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString);

        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return builder.Options;
    }

    private static TimeProvider Clock()
    {
        return new FixedTimeProvider(OccurredAt);
    }

    private async Task<LegacyGraph> SeedGraphAsync(
        bool clientInactive = false,
        bool deadlineCompleted = false)
    {
        var organization = new Organization(
            "Audit E2 Organization",
            "audit-e2-organization",
            SeededAt);
        var actorUser = new User(
            "Audit E2 Actor",
            "audit-e2-actor@example.test",
            SeededAt);
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actorUser.Id,
            OrganizationRole.Owner,
            SeededAt);
        var client = new Client(
            organization.Id,
            "Initial client",
            SeededAt);
        var process = new LegalProcess(
            organization.Id,
            client.Id,
            "Initial process",
            SeededAt);
        var deadline = new LegalDeadline(
            organization.Id,
            process.Id,
            "Initial deadline",
            InitialDueDate,
            SeededAt);

        if (clientInactive)
        {
            client.Deactivate();
        }

        if (deadlineCompleted)
        {
            deadline.Complete(SeededAt.AddMinutes(30));
        }

        await SeedAsync(
            organization,
            actorUser,
            actorMembership,
            client,
            process,
            deadline);
        return new LegacyGraph(
            organization,
            actorUser,
            actorMembership,
            client,
            process,
            deadline);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private async Task<AuditLog[]> FindAuditLogsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuditLogs.AsNoTracking().ToArrayAsync();
    }

    private async Task<int> CountClientsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.Clients.CountAsync();
    }

    private async Task<int> CountProcessesAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalProcesses.CountAsync();
    }

    private async Task<int> CountDeadlinesAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalDeadlines.CountAsync();
    }

    private async Task ChangeActorRoleAsync(
        Guid membershipId,
        OrganizationRole role)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembership membership = await dbContext
            .OrganizationMemberships
            .SingleAsync(candidate => candidate.Id == membershipId);
        membership.ChangeRole(role);
        await dbContext.SaveChangesAsync();
    }

    private async Task DeactivateMembershipAsync(Guid membershipId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembership membership = await dbContext
            .OrganizationMemberships
            .SingleAsync(candidate => candidate.Id == membershipId);
        membership.Deactivate();
        await dbContext.SaveChangesAsync();
    }

    private async Task DeactivateUserAsync(Guid userId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = await dbContext.Users.SingleAsync(
            candidate => candidate.Id == userId);
        user.Deactivate();
        await dbContext.SaveChangesAsync();
    }

    private async Task DeactivateOrganizationAsync(Guid organizationId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = await dbContext.Organizations.SingleAsync(
            candidate => candidate.Id == organizationId);
        organization.Deactivate();
        await dbContext.SaveChangesAsync();
    }

    private async Task<Client> FindClientAsync(Guid clientId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.Clients.AsNoTracking().SingleAsync(
            client => client.Id == clientId);
    }

    private async Task<LegalProcess> FindProcessAsync(Guid processId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalProcesses.AsNoTracking().SingleAsync(
            process => process.Id == processId);
    }

    private async Task<LegalDeadline> FindDeadlineAsync(Guid deadlineId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalDeadlines.AsNoTracking().SingleAsync(
            deadline => deadline.Id == deadlineId);
    }

    private sealed record LegacyGraph(
        Organization Organization,
        User ActorUser,
        OrganizationMembership ActorMembership,
        Client Client,
        LegalProcess Process,
        LegalDeadline Deadline);

    public enum AuditFailureOperation
    {
        ClientCreate = 0,
        ClientRename = 1,
        ClientDeactivate = 2,
        ClientReactivate = 3,
        ProcessCreate = 4,
        ProcessTitle = 5,
        DeadlineCreate = 6,
        DeadlineDetails = 7,
        DeadlineComplete = 8,
        DeadlineReopen = 9
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed class InvalidAuditDetailsInterceptor(string? detailsJson)
        : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            EnmaDbContext dbContext = Assert.IsType<EnmaDbContext>(
                eventData.Context);
            AuditLog auditLog = Assert.Single(
                dbContext.ChangeTracker.Entries<AuditLog>(),
                entry => entry.State == EntityState.Added).Entity;
            dbContext.Entry(auditLog)
                .Property<string?>("_detailsJson")
                .CurrentValue = detailsJson;
            return ValueTask.FromResult(result);
        }
    }
}
