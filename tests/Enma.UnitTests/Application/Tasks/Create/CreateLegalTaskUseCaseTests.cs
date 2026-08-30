using System.Reflection;
using Enma.Application.Authorization;
using Enma.Application.Tasks;
using Enma.Application.Tasks.Create;
using Enma.Domain.Organizations;
using Enma.Domain.Tasks;

namespace Enma.UnitTests.Application.Tasks.Create;

public sealed class CreateLegalTaskUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "dcf07c52-42f1-432c-8c44-f8b437999b24");
    private static readonly Guid OrganizationId = Guid.Parse(
        "97126e5e-6210-464a-b403-f05e2a14cc21");
    private static readonly Guid ActorMembershipId = Guid.Parse(
        "83e82a1f-3f07-4de5-817b-a06c19410a19");
    private static readonly Guid OtherMembershipId = Guid.Parse(
        "5821ccb5-0215-4462-a2d7-654723f1a7a2");
    private static readonly Guid OtherUserId = Guid.Parse(
        "04816a71-5aa8-40e6-bdd0-87f37b0800db");
    private static readonly Guid ProcessId = Guid.Parse(
        "29a1228c-d394-4789-8372-0a2192e79cbf");
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        14,
        21,
        15,
        30,
        TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsync_NoOrganizationAccess_ReturnsAccessDeniedFirst()
    {
        var accessLookup = new StubOrganizationAccessLookup(null);
        var processLookup = new StubProcessOwnershipLookup(false);
        var persistence = new StubLegalTaskCreationPersistence(
            CreateLockedState(OrganizationRole.Owner, AssignmentSelection.None));
        CreateLegalTaskUseCase useCase = CreateUseCase(
            accessLookup,
            processLookup,
            persistence);

        CreateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateCommand(ProcessId, OtherMembershipId));

        Assert.Same(CreateLegalTaskResult.AccessDenied, result);
        Assert.Equal(1, accessLookup.CallCount);
        Assert.Equal(0, processLookup.CallCount);
        Assert.Equal(0, persistence.CallCount);
    }

    [Theory]
    [MemberData(nameof(AuthorizedCreateMatrix))]
    public async Task ExecuteAsync_AuthorizedRoleAndAssignment_PersistsTask(
        OrganizationRole role,
        AssignmentSelection assignment)
    {
        Guid? assigneeMembershipId = GetAssigneeMembershipId(assignment);
        var persistence = new StubLegalTaskCreationPersistence(
            CreateLockedState(role, assignment));
        CreateLegalTaskUseCase useCase = CreateUseCase(
            new StubOrganizationAccessLookup(CreateAccess(role)),
            new StubProcessOwnershipLookup(true),
            persistence);

        CreateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateCommand(null, assigneeMembershipId));

        Assert.Equal(CreateLegalTaskResultStatus.Succeeded, result.Status);
        Assert.Equal(persistence.LegalTask?.Id, result.LegalTaskId);
        Assert.Equal(1, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_MemberAssigningOther_ReturnsAccessDeniedWithoutRelatedQueries()
    {
        var processLookup = new StubProcessOwnershipLookup(true);
        var persistence = new StubLegalTaskCreationPersistence(
            CreateLockedState(
                OrganizationRole.Member,
                AssignmentSelection.Other));
        CreateLegalTaskUseCase useCase = CreateUseCase(
            new StubOrganizationAccessLookup(
                CreateAccess(OrganizationRole.Member)),
            processLookup,
            persistence);

        CreateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateCommand(ProcessId, OtherMembershipId));

        Assert.Same(CreateLegalTaskResult.AccessDenied, result);
        Assert.Equal(0, processLookup.CallCount);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_AdministratorDemotedBeforeLockedDecision_DeniesOtherAssignment()
    {
        var persistence = new StubLegalTaskCreationPersistence(
            CreateLockedState(
                OrganizationRole.Member,
                AssignmentSelection.Other));
        CreateLegalTaskUseCase useCase = CreateUseCase(
            new StubOrganizationAccessLookup(
                CreateAccess(OrganizationRole.Administrator)),
            new StubProcessOwnershipLookup(true),
            persistence);

        CreateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateCommand(null, OtherMembershipId));

        Assert.Same(CreateLegalTaskResult.AccessDenied, result);
        Assert.Equal(1, persistence.CallCount);
        Assert.Null(persistence.LegalTask);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task ExecuteAsync_LockedAccessNoLongerActive_ReturnsAccessDenied(
        bool organizationActive,
        bool membershipActive,
        bool userActive)
    {
        LegalTaskCreationLockedState lockedState = CreateLockedState(
            OrganizationRole.Owner,
            AssignmentSelection.None,
            organizationActive,
            actorMembershipActive: membershipActive,
            actorUserActive: userActive);
        CreateLegalTaskUseCase useCase = CreateUseCase(
            new StubOrganizationAccessLookup(CreateAccess(OrganizationRole.Owner)),
            new StubProcessOwnershipLookup(true),
            new StubLegalTaskCreationPersistence(lockedState));

        CreateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateCommand(null, null));

        Assert.Same(CreateLegalTaskResult.AccessDenied, result);
    }

    [Theory]
    [InlineData(AssigneeAvailability.Missing)]
    [InlineData(AssigneeAvailability.CrossTenant)]
    [InlineData(AssigneeAvailability.InactiveMembership)]
    [InlineData(AssigneeAvailability.InactiveUser)]
    public async Task ExecuteAsync_AssigneeUnavailable_CollapsesToSingleResult(
        AssigneeAvailability availability)
    {
        LegalTaskCreationLockedState lockedState = CreateLockedState(
            OrganizationRole.Owner,
            AssignmentSelection.Other,
            assigneeAvailability: availability);
        var persistence = new StubLegalTaskCreationPersistence(lockedState);
        CreateLegalTaskUseCase useCase = CreateUseCase(
            new StubOrganizationAccessLookup(CreateAccess(OrganizationRole.Owner)),
            new StubProcessOwnershipLookup(true),
            persistence);

        CreateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateCommand(null, OtherMembershipId));

        Assert.Same(CreateLegalTaskResult.RelatedAssigneeUnavailable, result);
        Assert.Null(persistence.LegalTask);
    }

    [Fact]
    public async Task ExecuteAsync_MissingOrCrossTenantProcess_ReturnsSameUnavailableResult()
    {
        var missingProcessLookup = new StubProcessOwnershipLookup(false);
        var missingPersistence = new StubLegalTaskCreationPersistence(
            CreateLockedState(OrganizationRole.Owner, AssignmentSelection.None));
        CreateLegalTaskUseCase missingUseCase = CreateUseCase(
            new StubOrganizationAccessLookup(CreateAccess(OrganizationRole.Owner)),
            missingProcessLookup,
            missingPersistence);
        var crossTenantProcessLookup = new StubProcessOwnershipLookup(false);
        var crossTenantPersistence = new StubLegalTaskCreationPersistence(
            CreateLockedState(OrganizationRole.Owner, AssignmentSelection.None));
        CreateLegalTaskUseCase crossTenantUseCase = CreateUseCase(
            new StubOrganizationAccessLookup(CreateAccess(OrganizationRole.Owner)),
            crossTenantProcessLookup,
            crossTenantPersistence);

        CreateLegalTaskResult missing = await missingUseCase.ExecuteAsync(
            CreateCommand(Guid.NewGuid(), null));
        CreateLegalTaskResult crossTenant = await crossTenantUseCase.ExecuteAsync(
            CreateCommand(ProcessId, null));

        Assert.Same(CreateLegalTaskResult.RelatedProcessUnavailable, missing);
        Assert.Same(missing, crossTenant);
        Assert.Equal(0, missingPersistence.CallCount);
        Assert.Equal(0, crossTenantPersistence.CallCount);
    }

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public async Task ExecuteAsync_InvalidDomainInput_ReturnsControlledResult(
        CreateLegalTaskCommand command)
    {
        var persistence = new StubLegalTaskCreationPersistence(
            CreateLockedState(OrganizationRole.Owner, AssignmentSelection.None));
        CreateLegalTaskUseCase useCase = CreateUseCase(
            new StubOrganizationAccessLookup(CreateAccess(OrganizationRole.Owner)),
            new StubProcessOwnershipLookup(true),
            persistence);

        CreateLegalTaskResult result = await useCase.ExecuteAsync(command);

        Assert.Same(CreateLegalTaskResult.InvalidInput, result);
        Assert.Null(persistence.LegalTask);
    }

    [Fact]
    public async Task ExecuteAsync_AdminAssigningOther_UsesActorMembershipAsCreator()
    {
        var persistence = new StubLegalTaskCreationPersistence(
            CreateLockedState(
                OrganizationRole.Administrator,
                AssignmentSelection.Other));
        CreateLegalTaskUseCase useCase = CreateUseCase(
            new StubOrganizationAccessLookup(
                CreateAccess(OrganizationRole.Administrator)),
            new StubProcessOwnershipLookup(true),
            persistence);

        CreateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateCommand(ProcessId, OtherMembershipId));

        Assert.Equal(CreateLegalTaskResultStatus.Succeeded, result.Status);
        Assert.NotNull(persistence.LegalTask);
        Assert.Equal(ActorMembershipId, persistence.LegalTask.CreatedByMembershipId);
        Assert.Equal(OtherMembershipId, persistence.LegalTask.AssigneeMembershipId);
        Assert.NotEqual(UserId, persistence.LegalTask.CreatedByMembershipId);
        Assert.NotEqual(OtherUserId, persistence.LegalTask.AssigneeMembershipId);
    }

    [Fact]
    public async Task ExecuteAsync_DualMembershipContext_UsesSelectedOrganizationMembership()
    {
        Guid otherOrganizationMembershipId = Guid.Parse(
            "ba3a672e-02bf-4120-b78b-4685835c6664");
        var persistence = new StubLegalTaskCreationPersistence(
            CreateLockedState(OrganizationRole.Member, AssignmentSelection.Self));
        CreateLegalTaskUseCase useCase = CreateUseCase(
            new StubOrganizationAccessLookup(
                new OrganizationAccessLookupResult(
                    UserId,
                    OrganizationId,
                    ActorMembershipId,
                    OrganizationRole.Member)),
            new StubProcessOwnershipLookup(true),
            persistence);

        CreateLegalTaskResult result = await useCase.ExecuteAsync(
            CreateCommand(null, ActorMembershipId));

        Assert.Equal(CreateLegalTaskResultStatus.Succeeded, result.Status);
        Assert.Equal(ActorMembershipId, persistence.LegalTask?.CreatedByMembershipId);
        Assert.NotEqual(
            otherOrganizationMembershipId,
            persistence.LegalTask?.CreatedByMembershipId);
    }

    [Fact]
    public async Task ExecuteAsync_ValidInput_UsesServerTimeAndPreservesCreateFields()
    {
        var persistence = new StubLegalTaskCreationPersistence(
            CreateLockedState(OrganizationRole.Owner, AssignmentSelection.None));
        CreateLegalTaskUseCase useCase = CreateUseCase(
            new StubOrganizationAccessLookup(CreateAccess(OrganizationRole.Owner)),
            new StubProcessOwnershipLookup(true),
            persistence);
        var command = new CreateLegalTaskCommand(
            UserId,
            OrganizationId,
            "  Prepare defense  ",
            "  Review records  ",
            new DateOnly(2026, 9, 1),
            ProcessId,
            null);

        CreateLegalTaskResult result = await useCase.ExecuteAsync(command);

        Assert.Equal(CreateLegalTaskResultStatus.Succeeded, result.Status);
        Assert.NotNull(persistence.LegalTask);
        Assert.Equal(OrganizationId, persistence.LegalTask.OrganizationId);
        Assert.Equal("Prepare defense", persistence.LegalTask.Title);
        Assert.Equal("Review records", persistence.LegalTask.Description);
        Assert.Equal(command.DueDate, persistence.LegalTask.DueDate);
        Assert.Equal(ProcessId, persistence.LegalTask.ProcessId);
        Assert.Equal(CreatedAt, persistence.LegalTask.CreatedAt);
        Assert.Null(persistence.LegalTask.CompletedAt);
    }

    [Fact]
    public void CreateCommand_ContainsNoClientControlledAuthorityOrLifecycleFields()
    {
        string[] propertyNames = typeof(CreateLegalTaskCommand)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(
            [
                nameof(CreateLegalTaskCommand.AssigneeMembershipId),
                nameof(CreateLegalTaskCommand.Description),
                nameof(CreateLegalTaskCommand.DueDate),
                nameof(CreateLegalTaskCommand.OrganizationId),
                nameof(CreateLegalTaskCommand.ProcessId),
                nameof(CreateLegalTaskCommand.Title),
                nameof(CreateLegalTaskCommand.UserId)
            ],
            propertyNames);
        Assert.DoesNotContain("CreatedByMembershipId", propertyNames);
        Assert.DoesNotContain("Role", propertyNames);
        Assert.DoesNotContain("ClientId", propertyNames);
        Assert.DoesNotContain("CompletedAt", propertyNames);
        Assert.DoesNotContain("TenantId", propertyNames);
    }

    public static TheoryData<OrganizationRole, AssignmentSelection>
        AuthorizedCreateMatrix =>
        new()
        {
            { OrganizationRole.Owner, AssignmentSelection.None },
            { OrganizationRole.Owner, AssignmentSelection.Self },
            { OrganizationRole.Owner, AssignmentSelection.Other },
            { OrganizationRole.Administrator, AssignmentSelection.None },
            { OrganizationRole.Administrator, AssignmentSelection.Self },
            { OrganizationRole.Administrator, AssignmentSelection.Other },
            { OrganizationRole.Member, AssignmentSelection.None },
            { OrganizationRole.Member, AssignmentSelection.Self }
        };

    public static TheoryData<CreateLegalTaskCommand> InvalidCommands =>
        new()
        {
            CreateCommand(null, null) with { Title = "   " },
            CreateCommand(null, null) with { Description = new string('x', 2_001) },
            CreateCommand(null, null) with { DueDate = DateOnly.MinValue },
            CreateCommand(Guid.Empty, null),
            CreateCommand(null, Guid.Empty)
        };

    private static CreateLegalTaskUseCase CreateUseCase(
        IOrganizationAccessLookup accessLookup,
        IProcessOrganizationOwnershipLookup processLookup,
        ILegalTaskCreationPersistence persistence)
    {
        return new CreateLegalTaskUseCase(
            new OrganizationAccessAuthorization(accessLookup),
            processLookup,
            persistence,
            new FixedTimeProvider(CreatedAt));
    }

    private static CreateLegalTaskCommand CreateCommand(
        Guid? processId,
        Guid? assigneeMembershipId)
    {
        return new CreateLegalTaskCommand(
            UserId,
            OrganizationId,
            "Prepare defense",
            "Review records",
            new DateOnly(2026, 9, 1),
            processId,
            assigneeMembershipId);
    }

    private static OrganizationAccessLookupResult CreateAccess(
        OrganizationRole role)
    {
        return new OrganizationAccessLookupResult(
            UserId,
            OrganizationId,
            ActorMembershipId,
            role);
    }

    private static Guid? GetAssigneeMembershipId(AssignmentSelection assignment)
    {
        return assignment switch
        {
            AssignmentSelection.None => null,
            AssignmentSelection.Self => ActorMembershipId,
            AssignmentSelection.Other => OtherMembershipId,
            _ => throw new ArgumentOutOfRangeException(nameof(assignment))
        };
    }

    private static LegalTaskCreationLockedState CreateLockedState(
        OrganizationRole role,
        AssignmentSelection assignment,
        bool organizationActive = true,
        bool actorMembershipActive = true,
        bool actorUserActive = true,
        AssigneeAvailability assigneeAvailability = AssigneeAvailability.Available)
    {
        var actor = new LegalTaskCreationMemberState(
            ActorMembershipId,
            OrganizationId,
            UserId,
            role,
            actorMembershipActive,
            actorUserActive);
        LegalTaskCreationMemberState? assignee = assignment switch
        {
            AssignmentSelection.None => null,
            AssignmentSelection.Self => actor,
            AssignmentSelection.Other => CreateOtherAssignee(assigneeAvailability),
            _ => throw new ArgumentOutOfRangeException(nameof(assignment))
        };

        return new LegalTaskCreationLockedState(
            organizationActive,
            actor,
            assignee);
    }

    private static LegalTaskCreationMemberState? CreateOtherAssignee(
        AssigneeAvailability availability)
    {
        return availability switch
        {
            AssigneeAvailability.Missing => null,
            AssigneeAvailability.CrossTenant => new LegalTaskCreationMemberState(
                OtherMembershipId,
                Guid.NewGuid(),
                OtherUserId,
                OrganizationRole.Member,
                true,
                true),
            AssigneeAvailability.InactiveMembership =>
                new LegalTaskCreationMemberState(
                    OtherMembershipId,
                    OrganizationId,
                    OtherUserId,
                    OrganizationRole.Member,
                    false,
                    true),
            AssigneeAvailability.InactiveUser => new LegalTaskCreationMemberState(
                OtherMembershipId,
                OrganizationId,
                OtherUserId,
                OrganizationRole.Member,
                true,
                false),
            AssigneeAvailability.Available => new LegalTaskCreationMemberState(
                OtherMembershipId,
                OrganizationId,
                OtherUserId,
                OrganizationRole.Member,
                true,
                true),
            _ => throw new ArgumentOutOfRangeException(nameof(availability))
        };
    }

    public enum AssignmentSelection
    {
        None = 0,
        Self = 1,
        Other = 2
    }

    public enum AssigneeAvailability
    {
        Available = 0,
        Missing = 1,
        CrossTenant = 2,
        InactiveMembership = 3,
        InactiveUser = 4
    }

    private sealed class StubOrganizationAccessLookup(
        OrganizationAccessLookupResult? access) : IOrganizationAccessLookup
    {
        public int CallCount { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<OrganizationRole?>(access?.Role);
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Assert.Equal(UserId, userId);
            Assert.Equal(OrganizationId, organizationId);
            return Task.FromResult(access);
        }
    }

    private sealed class StubProcessOwnershipLookup(bool exists)
        : IProcessOrganizationOwnershipLookup
    {
        public int CallCount { get; private set; }

        public Task<bool> ExistsInOrganizationAsync(
            Guid processId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Assert.Equal(OrganizationId, organizationId);
            return Task.FromResult(exists);
        }
    }

    private sealed class StubLegalTaskCreationPersistence(
        LegalTaskCreationLockedState lockedState)
        : ILegalTaskCreationPersistence
    {
        public int CallCount { get; private set; }

        public LegalTask? LegalTask { get; private set; }

        public Task<LegalTaskCreationPersistenceResult> ExecuteAsync(
            LegalTaskCreationPersistenceRequest request,
            Func<LegalTaskCreationLockedState, LegalTaskCreationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Assert.Equal(UserId, request.UserId);
            Assert.Equal(OrganizationId, request.OrganizationId);
            Assert.Equal(ActorMembershipId, request.ActorMembershipId);

            LegalTaskCreationDecision decision = decide(lockedState);
            LegalTask = decision.LegalTask;

            return Task.FromResult(
                decision.Status == LegalTaskCreationDecisionStatus.Persist
                    ? LegalTaskCreationPersistenceResult.Succeeded(
                        Assert.IsType<LegalTask>(LegalTask).Id)
                    : LegalTaskCreationPersistenceResult.Rejected(decision.Status));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}
