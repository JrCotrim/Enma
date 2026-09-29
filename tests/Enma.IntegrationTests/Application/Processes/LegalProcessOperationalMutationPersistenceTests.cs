using Enma.Application.Authorization;
using Enma.Application.Organizations.Members.Lifecycle;
using Enma.Application.Processes.Details;
using Enma.Application.Processes.Responsible;
using Enma.Application.Processes.Status;
using Enma.Application.Validation;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Application.Processes;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalProcessOperationalMutationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string CnjFormatted = "0001234-56.2026.8.19.0001";
    private const string CnjDigits = "00012345620268190001";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        9,
        1,
        12,
        0,
        0,
        TimeSpan.Zero);

    private readonly AdvancingTimeProvider timeProvider = new(CreatedAt.AddDays(1));

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ChangeDetails_WithEffectiveChanges_PersistsAndAuditsOnlyChangedFields()
    {
        TestGraph graph = await SeedGraphAsync();
        Guid processId = graph.ProcessA1.Id;

        ChangeLegalProcessDetailsResult[] results =
        [
            await ChangeDetailsAsync(graph, processId, $"  {CnjFormatted} ", " 1ª Vara "),
            await ChangeDetailsAsync(graph, processId, CnjFormatted, "2ª Vara"),
            await ChangeDetailsAsync(graph, processId, CnjDigits, "2ª Vara"),
            await ChangeDetailsAsync(graph, processId, $" {CnjDigits} ", " 2ª Vara "),
            await ChangeDetailsAsync(graph, processId, null, "2ª Vara")
        ];

        Assert.All(
            results,
            result => Assert.Equal(ChangeLegalProcessDetailsResult.Succeeded, result));
        LegalProcess persisted = await GetProcessAsync(processId);
        Assert.Null(persisted.ProcessNumber);
        Assert.Null(persisted.NormalizedProcessNumber);
        Assert.Equal("2ª Vara", persisted.CourtOrAuthority);
        AssertUnchangedIdentity(graph.ProcessA1, persisted);

        List<AuditLog> auditLogs = await GetProcessAuditLogsAsync(processId);
        Assert.All(auditLogs, auditLog =>
        {
            Assert.Equal(AuditEventType.LegalProcessDetailsChanged, auditLog.EventType);
            Assert.Equal(AuditEntityType.LegalProcess, auditLog.EntityType);
            Assert.Equal(graph.OwnerMembership.Id, auditLog.ActorMembershipId);
            Assert.Equal(graph.OrganizationA.Id, auditLog.OrganizationId);
        });
        Assert.Equal(
            [
                [
                    LegalProcessChangedField.ProcessNumber,
                    LegalProcessChangedField.CourtOrAuthority
                ],
                [LegalProcessChangedField.CourtOrAuthority],
                [LegalProcessChangedField.ProcessNumber],
                [LegalProcessChangedField.ProcessNumber]
            ],
            auditLogs
                .Select(auditLog => Assert.IsType<LegalProcessDetailsChangedAuditDetails>(
                    auditLog.Details).ChangedFields.ToArray())
                .ToArray());
        Assert.All(
            await GetRawAuditDetailsAsync(processId),
            details =>
            {
                Assert.DoesNotContain("0001234", details, StringComparison.Ordinal);
                Assert.DoesNotContain("Vara", details, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task ChangeDetails_WithDuplicateNumberInTenant_ConflictsWithoutChangesOrAudit()
    {
        TestGraph graph = await SeedGraphAsync();

        ChangeLegalProcessDetailsResult first = await ChangeDetailsAsync(
            graph,
            graph.ProcessA1.Id,
            CnjFormatted,
            null);
        ChangeLegalProcessDetailsResult duplicate = await ChangeDetailsAsync(
            graph,
            graph.ProcessA2.Id,
            CnjDigits,
            "Should Not Persist");
        ChangeLegalProcessDetailsResult otherTenant = await ChangeDetailsAsync(
            graph.OtherUser.Id,
            graph.OrganizationB.Id,
            graph.ProcessB.Id,
            CnjFormatted,
            null);

        Assert.Equal(ChangeLegalProcessDetailsResult.Succeeded, first);
        Assert.Equal(ChangeLegalProcessDetailsResult.DuplicateProcessNumber, duplicate);
        Assert.Equal(ChangeLegalProcessDetailsResult.Succeeded, otherTenant);
        LegalProcess duplicateTarget = await GetProcessAsync(graph.ProcessA2.Id);
        Assert.Null(duplicateTarget.ProcessNumber);
        Assert.Null(duplicateTarget.CourtOrAuthority);
        Assert.Empty(await GetProcessAuditLogsAsync(graph.ProcessA2.Id));
        Assert.Equal(
            CnjDigits,
            (await GetProcessAsync(graph.ProcessB.Id)).NormalizedProcessNumber);
    }

    [Fact]
    public async Task ChangeDetails_ConcurrentSameNumberOnDifferentProcesses_ExactlyOneSucceeds()
    {
        TestGraph graph = await SeedGraphAsync();

        ChangeLegalProcessDetailsResult[] results = await Task.WhenAll(
            ChangeDetailsAsync(graph, graph.ProcessA1.Id, "ABC-1", null),
            ChangeDetailsAsync(graph, graph.ProcessA2.Id, "abc-1", null));

        Assert.Single(
            results,
            result => result == ChangeLegalProcessDetailsResult.Succeeded);
        Assert.Single(
            results,
            result => result == ChangeLegalProcessDetailsResult.DuplicateProcessNumber);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(
            1,
            await dbContext.LegalProcesses.CountAsync(
                legalProcess => legalProcess.NormalizedProcessNumber == "ABC-1"));
        Assert.Equal(
            1,
            await dbContext.AuditLogs.CountAsync(auditLog =>
                auditLog.EventType == AuditEventType.LegalProcessDetailsChanged));
    }

    [Fact]
    public async Task ChangeDetails_WithInvalidInput_ThrowsValidationWithoutChanges()
    {
        TestGraph graph = await SeedGraphAsync();

        await Assert.ThrowsAsync<RequestValidationException>(() => ChangeDetailsAsync(
            graph,
            graph.ProcessA1.Id,
            new string('A', 101),
            "Court"));
        await Assert.ThrowsAsync<RequestValidationException>(() => ChangeDetailsAsync(
            graph,
            graph.ProcessA1.Id,
            "ABC",
            new string('B', 201)));

        LegalProcess persisted = await GetProcessAsync(graph.ProcessA1.Id);
        Assert.Null(persisted.ProcessNumber);
        Assert.Null(persisted.CourtOrAuthority);
        Assert.Empty(await GetProcessAuditLogsAsync(graph.ProcessA1.Id));
    }

    [Fact]
    public async Task OperationalMutations_WithMemberRoleOrForeignProcess_DenyOrHideWithoutChanges()
    {
        TestGraph graph = await SeedGraphAsync();

        ChangeLegalProcessDetailsResult memberDetails = await ChangeDetailsAsync(
            graph.ResponsibleUser.Id,
            graph.OrganizationA.Id,
            graph.ProcessA1.Id,
            "ABC",
            null);
        ChangeLegalProcessStatusResult memberStatus = await ChangeStatusAsync(
            graph.ResponsibleUser.Id,
            graph.OrganizationA.Id,
            graph.ProcessA1.Id,
            "closed");
        ChangeLegalProcessResponsibleResult memberResponsible =
            await ChangeResponsibleAsync(
                graph.ResponsibleUser.Id,
                graph.OrganizationA.Id,
                graph.ProcessA1.Id,
                graph.ResponsibleMembership.Id);
        ChangeLegalProcessDetailsResult foreignDetails = await ChangeDetailsAsync(
            graph,
            graph.ProcessB.Id,
            "ABC",
            null);
        ChangeLegalProcessStatusResult foreignStatus = await ChangeStatusAsync(
            graph,
            graph.ProcessB.Id,
            "closed");
        ChangeLegalProcessResponsibleResult foreignResponsible =
            await ChangeResponsibleAsync(graph, graph.ProcessB.Id, null);
        ChangeLegalProcessStatusResult missingStatus = await ChangeStatusAsync(
            graph,
            Guid.NewGuid(),
            "closed");

        Assert.Equal(ChangeLegalProcessDetailsResult.AccessDenied, memberDetails);
        Assert.Equal(ChangeLegalProcessStatusResult.AccessDenied, memberStatus);
        Assert.Equal(
            ChangeLegalProcessResponsibleResult.AccessDenied,
            memberResponsible);
        Assert.Equal(ChangeLegalProcessDetailsResult.NotFound, foreignDetails);
        Assert.Equal(ChangeLegalProcessStatusResult.NotFound, foreignStatus);
        Assert.Equal(ChangeLegalProcessResponsibleResult.NotFound, foreignResponsible);
        Assert.Equal(ChangeLegalProcessStatusResult.NotFound, missingStatus);
        LegalProcess processA1 = await GetProcessAsync(graph.ProcessA1.Id);
        LegalProcess processB = await GetProcessAsync(graph.ProcessB.Id);
        Assert.Equal(LegalProcessStatus.InProgress, processA1.Status);
        Assert.Null(processA1.ProcessNumber);
        Assert.Null(processA1.ResponsibleMembershipId);
        Assert.Equal(LegalProcessStatus.InProgress, processB.Status);
        Assert.Null(processB.ProcessNumber);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task ChangeStatus_WithValidTransitions_PersistsAndAuditsOldAndNewStatus()
    {
        TestGraph graph = await SeedGraphAsync();
        Guid processId = graph.ProcessA1.Id;

        ChangeLegalProcessStatusResult[] results =
        [
            await ChangeStatusAsync(graph, processId, "suspended"),
            await ChangeStatusAsync(graph, processId, "suspended"),
            await ChangeStatusAsync(graph, processId, "inProgress"),
            await ChangeStatusAsync(graph, processId, "closed"),
            await ChangeStatusAsync(graph, processId, "closed"),
            await ChangeStatusAsync(graph, processId, "inProgress")
        ];

        Assert.All(
            results,
            result => Assert.Equal(ChangeLegalProcessStatusResult.Succeeded, result));
        LegalProcess persisted = await GetProcessAsync(processId);
        Assert.Equal(LegalProcessStatus.InProgress, persisted.Status);
        AssertUnchangedIdentity(graph.ProcessA1, persisted);
        List<AuditLog> auditLogs = await GetProcessAuditLogsAsync(processId);
        Assert.All(auditLogs, auditLog => Assert.Equal(
            AuditEventType.LegalProcessStatusChanged,
            auditLog.EventType));
        Assert.Equal(
            [
                (LegalProcessStatus.InProgress, LegalProcessStatus.Suspended),
                (LegalProcessStatus.Suspended, LegalProcessStatus.InProgress),
                (LegalProcessStatus.InProgress, LegalProcessStatus.Closed),
                (LegalProcessStatus.Closed, LegalProcessStatus.InProgress)
            ],
            auditLogs
                .Select(auditLog => Assert.IsType<LegalProcessStatusChangedAuditDetails>(
                    auditLog.Details))
                .Select(details => (details.OldStatus, details.NewStatus))
                .ToArray());
    }

    [Fact]
    public async Task ChangeStatus_FromClosedToSuspended_IsRejectedWithoutChanges()
    {
        TestGraph graph = await SeedGraphAsync();
        await ChangeStatusAsync(graph, graph.ProcessA1.Id, "closed");

        ChangeLegalProcessStatusResult result = await ChangeStatusAsync(
            graph,
            graph.ProcessA1.Id,
            "suspended");

        Assert.Equal(ChangeLegalProcessStatusResult.StatusTransitionNotAllowed, result);
        Assert.Equal(
            LegalProcessStatus.Closed,
            (await GetProcessAsync(graph.ProcessA1.Id)).Status);
        Assert.Single(await GetProcessAuditLogsAsync(graph.ProcessA1.Id));
    }

    [Theory]
    [InlineData("InProgress")]
    [InlineData("archived")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ChangeStatus_WithUnknownValue_ThrowsValidationWithoutChanges(
        string? status)
    {
        TestGraph graph = await SeedGraphAsync();

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            ChangeStatusAsync(graph, graph.ProcessA1.Id, status));

        Assert.Equal(
            LegalProcessStatus.InProgress,
            (await GetProcessAsync(graph.ProcessA1.Id)).Status);
    }

    [Theory]
    [InlineData(ResponsibleAvailability.Available)]
    [InlineData(ResponsibleAvailability.InactiveMembership)]
    [InlineData(ResponsibleAvailability.InactiveUser)]
    public async Task ChangeStatus_ReopenWithCurrentResponsible_RequiresAvailableResponsible(
        ResponsibleAvailability availability)
    {
        TestGraph graph = await SeedGraphAsync();
        Guid processId = graph.ProcessA1.Id;
        Assert.Equal(
            ChangeLegalProcessResponsibleResult.Succeeded,
            await ChangeResponsibleAsync(
                graph,
                processId,
                graph.ResponsibleMembership.Id));
        Assert.Equal(
            ChangeLegalProcessStatusResult.Succeeded,
            await ChangeStatusAsync(graph, processId, "closed"));
        await MakeResponsibleUnavailableAsync(graph, availability);

        ChangeLegalProcessStatusResult result = await ChangeStatusAsync(
            graph,
            processId,
            "inProgress");

        LegalProcess persisted = await GetProcessAsync(processId);
        int statusEvents = (await GetProcessAuditLogsAsync(processId)).Count(
            auditLog => auditLog.EventType == AuditEventType.LegalProcessStatusChanged);

        if (availability == ResponsibleAvailability.Available)
        {
            Assert.Equal(ChangeLegalProcessStatusResult.Succeeded, result);
            Assert.Equal(LegalProcessStatus.InProgress, persisted.Status);
            Assert.Equal(2, statusEvents);
        }
        else
        {
            Assert.Equal(
                ChangeLegalProcessStatusResult.CurrentResponsibleUnavailable,
                result);
            Assert.Equal(LegalProcessStatus.Closed, persisted.Status);
            Assert.Equal(1, statusEvents);
        }

        Assert.Equal(graph.ResponsibleMembership.Id, persisted.ResponsibleMembershipId);
    }

    [Fact]
    public async Task ChangeStatus_ReopenWithoutResponsible_Succeeds()
    {
        TestGraph graph = await SeedGraphAsync();
        await ChangeStatusAsync(graph, graph.ProcessA1.Id, "closed");

        ChangeLegalProcessStatusResult result = await ChangeStatusAsync(
            graph,
            graph.ProcessA1.Id,
            "inProgress");

        Assert.Equal(ChangeLegalProcessStatusResult.Succeeded, result);
        Assert.Equal(
            LegalProcessStatus.InProgress,
            (await GetProcessAsync(graph.ProcessA1.Id)).Status);
    }

    [Fact]
    public async Task ChangeResponsible_SetClearAndNoOp_AuditsOnlyEffectiveChanges()
    {
        TestGraph graph = await SeedGraphAsync();
        Guid processId = graph.ProcessA1.Id;

        ChangeLegalProcessResponsibleResult[] results =
        [
            await ChangeResponsibleAsync(graph, processId, graph.ResponsibleMembership.Id),
            await ChangeResponsibleAsync(graph, processId, graph.ResponsibleMembership.Id),
            await ChangeResponsibleAsync(graph, processId, null),
            await ChangeResponsibleAsync(graph, processId, null)
        ];

        Assert.All(
            results,
            result => Assert.Equal(ChangeLegalProcessResponsibleResult.Succeeded, result));
        LegalProcess persisted = await GetProcessAsync(processId);
        Assert.Null(persisted.ResponsibleMembershipId);
        AssertUnchangedIdentity(graph.ProcessA1, persisted);
        Assert.Equal(
            [
                (null, graph.ResponsibleMembership.Id),
                ((Guid?)graph.ResponsibleMembership.Id, (Guid?)null)
            ],
            (await GetProcessAuditLogsAsync(processId))
                .Select(auditLog =>
                    Assert.IsType<LegalProcessResponsibleChangedAuditDetails>(
                        auditLog.Details))
                .Select(details => (
                    details.OldResponsibleMembershipId,
                    details.NewResponsibleMembershipId))
                .ToArray());
    }

    [Theory]
    [InlineData(UnavailableResponsible.OtherTenant)]
    [InlineData(UnavailableResponsible.Missing)]
    [InlineData(UnavailableResponsible.InactiveMembership)]
    [InlineData(UnavailableResponsible.InactiveUser)]
    public async Task ChangeResponsible_WithUnavailableMember_IsRejectedNeutrally(
        UnavailableResponsible unavailable)
    {
        TestGraph graph = await SeedGraphAsync();
        Guid requestedMembershipId = unavailable switch
        {
            UnavailableResponsible.OtherTenant => graph.OtherMembership.Id,
            UnavailableResponsible.Missing => Guid.NewGuid(),
            _ => graph.ResponsibleMembership.Id
        };

        if (unavailable == UnavailableResponsible.InactiveMembership)
        {
            await MakeResponsibleUnavailableAsync(
                graph,
                ResponsibleAvailability.InactiveMembership);
        }
        else if (unavailable == UnavailableResponsible.InactiveUser)
        {
            await MakeResponsibleUnavailableAsync(
                graph,
                ResponsibleAvailability.InactiveUser);
        }

        ChangeLegalProcessResponsibleResult result = await ChangeResponsibleAsync(
            graph,
            graph.ProcessA1.Id,
            requestedMembershipId);

        Assert.Equal(
            ChangeLegalProcessResponsibleResult.RelatedResponsibleUnavailable,
            result);
        Assert.Null((await GetProcessAsync(graph.ProcessA1.Id)).ResponsibleMembershipId);
        Assert.Empty(await GetProcessAuditLogsAsync(graph.ProcessA1.Id));
    }

    [Fact]
    public async Task ChangeResponsible_OnClosedProcess_AllowsReplacementAndRemoval()
    {
        TestGraph graph = await SeedGraphAsync();
        Guid processId = graph.ProcessA1.Id;
        await ChangeResponsibleAsync(graph, processId, graph.ResponsibleMembership.Id);
        await ChangeStatusAsync(graph, processId, "closed");
        await MakeResponsibleUnavailableAsync(
            graph,
            ResponsibleAvailability.InactiveMembership);

        ChangeLegalProcessResponsibleResult replaceWithOwner =
            await ChangeResponsibleAsync(graph, processId, graph.OwnerMembership.Id);
        ChangeLegalProcessResponsibleResult remove =
            await ChangeResponsibleAsync(graph, processId, null);

        Assert.Equal(ChangeLegalProcessResponsibleResult.Succeeded, replaceWithOwner);
        Assert.Equal(ChangeLegalProcessResponsibleResult.Succeeded, remove);
        LegalProcess persisted = await GetProcessAsync(processId);
        Assert.Equal(LegalProcessStatus.Closed, persisted.Status);
        Assert.Null(persisted.ResponsibleMembershipId);
    }

    [Fact]
    public async Task ChangeResponsible_WithEmptyIdentifier_ReturnsInvalidInput()
    {
        TestGraph graph = await SeedGraphAsync();

        ChangeLegalProcessResponsibleResult result = await ChangeResponsibleAsync(
            graph,
            graph.ProcessA1.Id,
            Guid.Empty);

        Assert.Equal(ChangeLegalProcessResponsibleResult.InvalidInput, result);
        Assert.Null((await GetProcessAsync(graph.ProcessA1.Id)).ResponsibleMembershipId);
    }

    [Theory]
    [InlineData(null, OrganizationMemberLifecycleMutationPersistenceResult.Succeeded)]
    [InlineData(
        "inProgress",
        OrganizationMemberLifecycleMutationPersistenceResult.ActiveAssignmentsConflict)]
    [InlineData(
        "suspended",
        OrganizationMemberLifecycleMutationPersistenceResult.ActiveAssignmentsConflict)]
    [InlineData("closed", OrganizationMemberLifecycleMutationPersistenceResult.Succeeded)]
    public async Task DeactivateMember_ResponsibleForProcess_ConflictsOnlyWhileProcessIsOpen(
        string? processStatus,
        OrganizationMemberLifecycleMutationPersistenceResult expected)
    {
        TestGraph graph = await SeedGraphAsync();

        if (processStatus is not null)
        {
            await ChangeResponsibleAsync(
                graph,
                graph.ProcessA1.Id,
                graph.ResponsibleMembership.Id);

            if (processStatus != "inProgress")
            {
                await ChangeStatusAsync(graph, graph.ProcessA1.Id, processStatus);
            }
        }

        OrganizationMemberLifecycleMutationPersistenceResult result =
            await new OrganizationMemberLifecycleMutationPersistence(
                    CreateOptions(),
                    timeProvider)
                .ExecuteAsync(new OrganizationMemberLifecycleMutationPersistenceRequest(
                    graph.OwnerUser.Id,
                    graph.OrganizationA.Id,
                    graph.OwnerMembership.Id,
                    graph.ResponsibleMembership.Id,
                    OrganizationMemberLifecycleOperation.Deactivate));

        Assert.Equal(expected, result);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(
            expected != OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
            await dbContext.OrganizationMemberships
                .Where(membership => membership.Id == graph.ResponsibleMembership.Id)
                .Select(membership => membership.IsActive)
                .SingleAsync());
    }

    private Task<ChangeLegalProcessDetailsResult> ChangeDetailsAsync(
        TestGraph graph,
        Guid processId,
        string? processNumber,
        string? courtOrAuthority)
    {
        return ChangeDetailsAsync(
            graph.OwnerUser.Id,
            graph.OrganizationA.Id,
            processId,
            processNumber,
            courtOrAuthority);
    }

    private async Task<ChangeLegalProcessDetailsResult> ChangeDetailsAsync(
        Guid userId,
        Guid organizationId,
        Guid processId,
        string? processNumber,
        string? courtOrAuthority)
    {
        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        var useCase = new ChangeLegalProcessDetailsUseCase(
            CreateAuthorization(authorizationContext),
            CreatePersistence());

        return await useCase.ExecuteAsync(new ChangeLegalProcessDetailsCommand(
            userId,
            organizationId,
            processId,
            processNumber,
            courtOrAuthority));
    }

    private Task<ChangeLegalProcessStatusResult> ChangeStatusAsync(
        TestGraph graph,
        Guid processId,
        string? status)
    {
        return ChangeStatusAsync(
            graph.OwnerUser.Id,
            graph.OrganizationA.Id,
            processId,
            status);
    }

    private async Task<ChangeLegalProcessStatusResult> ChangeStatusAsync(
        Guid userId,
        Guid organizationId,
        Guid processId,
        string? status)
    {
        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        var useCase = new ChangeLegalProcessStatusUseCase(
            CreateAuthorization(authorizationContext),
            CreatePersistence());

        return await useCase.ExecuteAsync(new ChangeLegalProcessStatusCommand(
            userId,
            organizationId,
            processId,
            status));
    }

    private Task<ChangeLegalProcessResponsibleResult> ChangeResponsibleAsync(
        TestGraph graph,
        Guid processId,
        Guid? responsibleMembershipId)
    {
        return ChangeResponsibleAsync(
            graph.OwnerUser.Id,
            graph.OrganizationA.Id,
            processId,
            responsibleMembershipId);
    }

    private async Task<ChangeLegalProcessResponsibleResult> ChangeResponsibleAsync(
        Guid userId,
        Guid organizationId,
        Guid processId,
        Guid? responsibleMembershipId)
    {
        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        var useCase = new ChangeLegalProcessResponsibleUseCase(
            CreateAuthorization(authorizationContext),
            CreatePersistence());

        return await useCase.ExecuteAsync(new ChangeLegalProcessResponsibleCommand(
            userId,
            organizationId,
            processId,
            responsibleMembershipId));
    }

    private static ProcessActionAuthorization CreateAuthorization(
        EnmaDbContext dbContext)
    {
        return new ProcessActionAuthorization(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(dbContext)));
    }

    private LegalProcessMutationPersistence CreatePersistence()
    {
        return new LegalProcessMutationPersistence(CreateOptions(), timeProvider);
    }

    private DbContextOptions<EnmaDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
    }

    private async Task MakeResponsibleUnavailableAsync(
        TestGraph graph,
        ResponsibleAvailability availability)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        if (availability == ResponsibleAvailability.InactiveMembership)
        {
            OrganizationMembership membership = await dbContext.OrganizationMemberships
                .SingleAsync(candidate => candidate.Id == graph.ResponsibleMembership.Id);
            membership.Deactivate();
        }
        else if (availability == ResponsibleAvailability.InactiveUser)
        {
            User user = await dbContext.Users
                .SingleAsync(candidate => candidate.Id == graph.ResponsibleUser.Id);
            user.Deactivate();
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task<LegalProcess> GetProcessAsync(Guid processId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalProcesses
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == processId);
    }

    private async Task<List<AuditLog>> GetProcessAuditLogsAsync(Guid processId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuditLogs
            .AsNoTracking()
            .Where(auditLog => auditLog.EntityId == processId)
            .OrderBy(auditLog => auditLog.OccurredAt)
            .ToListAsync();
    }

    private async Task<List<string>> GetRawAuditDetailsAsync(Guid processId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        List<string?> details = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(auditLog => auditLog.EntityId == processId)
            .Select(auditLog => EF.Property<string?>(auditLog, "_detailsJson"))
            .ToListAsync();

        return details.Select(value => Assert.IsType<string>(value)).ToList();
    }

    private static void AssertUnchangedIdentity(
        LegalProcess original,
        LegalProcess persisted)
    {
        Assert.Equal(original.Id, persisted.Id);
        Assert.Equal(original.OrganizationId, persisted.OrganizationId);
        Assert.Equal(original.ClientId, persisted.ClientId);
        Assert.Equal(original.Title, persisted.Title);
        Assert.Equal(original.CreatedAt, persisted.CreatedAt);
    }

    private async Task<TestGraph> SeedGraphAsync()
    {
        var organizationA = new Organization(
            "Operational A",
            $"operational-a-{Guid.NewGuid():N}",
            CreatedAt);
        var organizationB = new Organization(
            "Operational B",
            $"operational-b-{Guid.NewGuid():N}",
            CreatedAt);
        var ownerUser = new User(
            "Operational Owner",
            $"operational-owner-{Guid.NewGuid():N}@example.test",
            CreatedAt);
        var responsibleUser = new User(
            "Operational Responsible",
            $"operational-responsible-{Guid.NewGuid():N}@example.test",
            CreatedAt);
        var otherUser = new User(
            "Operational Other",
            $"operational-other-{Guid.NewGuid():N}@example.test",
            CreatedAt);
        var ownerMembership = new OrganizationMembership(
            organizationA.Id,
            ownerUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var responsibleMembership = new OrganizationMembership(
            organizationA.Id,
            responsibleUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        var otherMembership = new OrganizationMembership(
            organizationB.Id,
            otherUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var clientA = new Client(organizationA.Id, "Operational Client A", CreatedAt);
        var clientB = new Client(organizationB.Id, "Operational Client B", CreatedAt);
        var processA1 = new LegalProcess(
            organizationA.Id,
            clientA.Id,
            "Operational Process A1",
            CreatedAt);
        var processA2 = new LegalProcess(
            organizationA.Id,
            clientA.Id,
            "Operational Process A2",
            CreatedAt);
        var processB = new LegalProcess(
            organizationB.Id,
            clientB.Id,
            "Operational Process B",
            CreatedAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(
            organizationA,
            organizationB,
            ownerUser,
            responsibleUser,
            otherUser,
            ownerMembership,
            responsibleMembership,
            otherMembership,
            clientA,
            clientB,
            processA1,
            processA2,
            processB);
        await dbContext.SaveChangesAsync();

        return new TestGraph(
            organizationA,
            organizationB,
            ownerUser,
            ownerMembership,
            responsibleUser,
            responsibleMembership,
            otherUser,
            otherMembership,
            processA1,
            processA2,
            processB);
    }

    public enum ResponsibleAvailability
    {
        Available = 0,
        InactiveMembership = 1,
        InactiveUser = 2
    }

    public enum UnavailableResponsible
    {
        OtherTenant = 0,
        Missing = 1,
        InactiveMembership = 2,
        InactiveUser = 3
    }

    private sealed record TestGraph(
        Organization OrganizationA,
        Organization OrganizationB,
        User OwnerUser,
        OrganizationMembership OwnerMembership,
        User ResponsibleUser,
        OrganizationMembership ResponsibleMembership,
        User OtherUser,
        OrganizationMembership OtherMembership,
        LegalProcess ProcessA1,
        LegalProcess ProcessA2,
        LegalProcess ProcessB);

    private sealed class AdvancingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private long _ticks;

        public override DateTimeOffset GetUtcNow()
        {
            return start.AddSeconds(Interlocked.Increment(ref _ticks));
        }
    }
}
