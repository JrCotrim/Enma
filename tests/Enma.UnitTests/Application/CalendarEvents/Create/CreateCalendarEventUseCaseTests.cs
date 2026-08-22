using System.Reflection;
using Enma.Application.Authorization;
using Enma.Application.CalendarEvents;
using Enma.Application.CalendarEvents.Create;
using Enma.Application.Processes;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.CalendarEvents.Create;

public sealed class CreateCalendarEventUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "85f2125f-0763-48b1-bd13-fba42cd7bb8e");
    private static readonly Guid OrganizationId = Guid.Parse(
        "d27bf9ad-fd8b-46cb-82ee-c32dfe38de8d");
    private static readonly Guid ActorMembershipId = Guid.Parse(
        "03f72ac6-664e-430a-ad25-146a96fd84f9");
    private static readonly Guid OtherMembershipId = Guid.Parse(
        "915cfc3d-d6c9-4591-b4cf-8bb812137673");
    private static readonly Guid OtherUserId = Guid.Parse(
        "d5ce764e-cd68-44f1-b2a6-cfc2528c2d19");
    private static readonly Guid ClientId = Guid.Parse(
        "e8c94832-80a1-4de5-ac13-5a53fc9bf691");
    private static readonly Guid ProcessId = Guid.Parse(
        "7334351e-0169-4dd2-b7fd-0ce361809fb0");
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        22,
        18,
        30,
        0,
        TimeSpan.Zero);

    [Theory]
    [MemberData(nameof(AuthorizedCreateMatrix))]
    public async Task ExecuteAsync_AuthorizedRoleAndAssignment_CreatesEvent(
        OrganizationRole role,
        AssignmentSelection assignment)
    {
        Guid? assigneeId = ResolveAssignee(assignment);
        var persistence = new StubCreationPersistence(
            CreateLockedState(role, assigneeId));
        CreateCalendarEventUseCase useCase = CreateUseCase(
            role,
            persistence: persistence);

        CreateCalendarEventResult result = await useCase.ExecuteAsync(
            CreateCommand(assigneeMembershipId: assigneeId));

        Assert.Equal(CreateCalendarEventResultStatus.Created, result.Status);
        Assert.Equal(persistence.CalendarEvent?.Id, result.CalendarEventId);
        Assert.Equal(ActorMembershipId, persistence.CalendarEvent?.CreatedByMembershipId);
        Assert.Equal(assigneeId, persistence.CalendarEvent?.AssigneeMembershipId);
    }

    [Fact]
    public async Task ExecuteAsync_MemberAssigningOther_DeniesBeforeRelatedLookups()
    {
        var clientLookup = new StubClientLookup(true);
        var processLookup = new StubProcessLookup(true);
        var persistence = new StubCreationPersistence(
            CreateLockedState(OrganizationRole.Member, OtherMembershipId));
        CreateCalendarEventUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            clientLookup,
            processLookup,
            persistence);

        CreateCalendarEventResult result = await useCase.ExecuteAsync(
            CreateCommand(
                clientId: ClientId,
                assigneeMembershipId: OtherMembershipId));

        Assert.Same(CreateCalendarEventResult.AccessDenied, result);
        Assert.Equal(0, clientLookup.CallCount);
        Assert.Equal(0, processLookup.CallCount);
        Assert.Equal(0, persistence.CallCount);
    }

    [Theory]
    [InlineData(AssociationSelection.General)]
    [InlineData(AssociationSelection.Client)]
    [InlineData(AssociationSelection.Process)]
    public async Task ExecuteAsync_ValidAssociation_CreatesExpectedEvent(
        AssociationSelection association)
    {
        Guid? clientId = association == AssociationSelection.Client ? ClientId : null;
        Guid? processId = association == AssociationSelection.Process ? ProcessId : null;
        var persistence = new StubCreationPersistence(
            CreateLockedState(
                OrganizationRole.Owner,
                assigneeMembershipId: null,
                isClientAvailable: clientId is null ? null : true,
                isProcessAvailable: processId is null ? null : true));
        CreateCalendarEventUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence: persistence);

        CreateCalendarEventResult result = await useCase.ExecuteAsync(
            CreateCommand(clientId, processId));

        Assert.Equal(CreateCalendarEventResultStatus.Created, result.Status);
        Assert.Equal(clientId, persistence.CalendarEvent?.ClientId);
        Assert.Equal(processId, persistence.CalendarEvent?.ProcessId);
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroOffsets_PersistsSameUtcInstants()
    {
        var startsAt = new DateTimeOffset(
            2026,
            9,
            10,
            9,
            45,
            0,
            TimeSpan.FromHours(-3));
        var endsAt = new DateTimeOffset(
            2026,
            9,
            10,
            15,
            15,
            0,
            TimeSpan.FromHours(2));
        var persistence = new StubCreationPersistence(
            CreateLockedState(OrganizationRole.Owner, null));
        CreateCalendarEventUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence: persistence);

        CreateCalendarEventResult result = await useCase.ExecuteAsync(
            CreateCommand() with { StartsAt = startsAt, EndsAt = endsAt });

        Assert.Equal(CreateCalendarEventResultStatus.Created, result.Status);
        Assert.Equal(startsAt.UtcDateTime, persistence.CalendarEvent?.StartsAt.UtcDateTime);
        Assert.Equal(endsAt.UtcDateTime, persistence.CalendarEvent?.EndsAt.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, persistence.CalendarEvent?.StartsAt.Offset);
        Assert.Equal(TimeSpan.Zero, persistence.CalendarEvent?.EndsAt.Offset);
        Assert.Equal(CreatedAt, persistence.CalendarEvent?.CreatedAt);
    }

    [Theory]
    [InlineData(RelatedResource.Client)]
    [InlineData(RelatedResource.Process)]
    public async Task ExecuteAsync_MissingOrCrossTenantRelation_ReturnsGenericUnavailable(
        RelatedResource resource)
    {
        var clientLookup = new StubClientLookup(resource != RelatedResource.Client);
        var processLookup = new StubProcessLookup(resource != RelatedResource.Process);
        var persistence = new StubCreationPersistence(
            CreateLockedState(OrganizationRole.Owner, null));
        CreateCalendarEventUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            clientLookup,
            processLookup,
            persistence);
        CreateCalendarEventCommand command = resource == RelatedResource.Client
            ? CreateCommand(clientId: ClientId)
            : CreateCommand(processId: ProcessId);

        CreateCalendarEventResult result = await useCase.ExecuteAsync(command);

        Assert.Equal(
            resource == RelatedResource.Client
                ? CreateCalendarEventResultStatus.RelatedClientUnavailable
                : CreateCalendarEventResultStatus.RelatedProcessUnavailable,
            result.Status);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_RelationOrAssigneeBecomesUnavailableUnderLock_DoesNotPersist()
    {
        var clientPersistence = new StubCreationPersistence(
            CreateLockedState(
                OrganizationRole.Owner,
                null,
                isClientAvailable: false));
        var assigneePersistence = new StubCreationPersistence(
            CreateLockedState(
                OrganizationRole.Owner,
                OtherMembershipId,
                assigneeAvailable: false));

        CreateCalendarEventResult clientResult = await CreateUseCase(
                OrganizationRole.Owner,
                persistence: clientPersistence)
            .ExecuteAsync(CreateCommand(clientId: ClientId));
        CreateCalendarEventResult assigneeResult = await CreateUseCase(
                OrganizationRole.Owner,
                persistence: assigneePersistence)
            .ExecuteAsync(CreateCommand(assigneeMembershipId: OtherMembershipId));

        Assert.Same(CreateCalendarEventResult.RelatedClientUnavailable, clientResult);
        Assert.Same(
            CreateCalendarEventResult.RelatedAssigneeUnavailable,
            assigneeResult);
        Assert.Null(clientPersistence.CalendarEvent);
        Assert.Null(assigneePersistence.CalendarEvent);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task ExecuteAsync_InactiveLockedActorOrOrganization_Denies(
        bool organizationActive,
        bool membershipActive,
        bool userActive)
    {
        var persistence = new StubCreationPersistence(
            CreateLockedState(
                OrganizationRole.Owner,
                null,
                organizationActive,
                membershipActive,
                userActive));
        CreateCalendarEventUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence: persistence);

        CreateCalendarEventResult result = await useCase.ExecuteAsync(
            CreateCommand());

        Assert.Same(CreateCalendarEventResult.AccessDenied, result);
        Assert.Null(persistence.CalendarEvent);
    }

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public async Task ExecuteAsync_InvalidInput_ReturnsControlledResult(
        CreateCalendarEventCommand command)
    {
        var persistence = new StubCreationPersistence(
            CreateLockedState(OrganizationRole.Owner, null));
        CreateCalendarEventUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence: persistence);

        CreateCalendarEventResult result = await useCase.ExecuteAsync(command);

        Assert.Same(CreateCalendarEventResult.InvalidInput, result);
        Assert.Null(persistence.CalendarEvent);
    }

    [Fact]
    public void Command_DoesNotExposeCreatorOrAuthorityFields()
    {
        string[] names = typeof(CreateCalendarEventCommand)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("CreatedByMembershipId", names);
        Assert.DoesNotContain("CreatedAt", names);
        Assert.DoesNotContain("Role", names);
        Assert.DoesNotContain("TenantId", names);
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

    public static TheoryData<CreateCalendarEventCommand> InvalidCommands =>
        new()
        {
            CreateCommand() with { Title = "   " },
            CreateCommand() with { Description = new string('d', 2_001) },
            CreateCommand() with { Location = new string('l', 256) },
            CreateCommand() with { EndsAt = CreateCommand().StartsAt },
            CreateCommand(clientId: Guid.Empty),
            CreateCommand(processId: Guid.Empty),
            CreateCommand(assigneeMembershipId: Guid.Empty),
            CreateCommand(clientId: ClientId, processId: ProcessId)
        };

    private static CreateCalendarEventUseCase CreateUseCase(
        OrganizationRole? role,
        StubClientLookup? clientLookup = null,
        StubProcessLookup? processLookup = null,
        StubCreationPersistence? persistence = null)
    {
        return new CreateCalendarEventUseCase(
            new CalendarEventAccessAuthorization(
                new OrganizationAccessAuthorization(new StubAccessLookup(role))),
            new CalendarEventActionAuthorization(),
            clientLookup ?? new StubClientLookup(true),
            processLookup ?? new StubProcessLookup(true),
            persistence ?? new StubCreationPersistence(
                CreateLockedState(role ?? OrganizationRole.Owner, null)),
            new FixedTimeProvider(CreatedAt));
    }

    private static CreateCalendarEventCommand CreateCommand(
        Guid? clientId = null,
        Guid? processId = null,
        Guid? assigneeMembershipId = null)
    {
        return new CreateCalendarEventCommand(
            UserId,
            OrganizationId,
            "  Strategy meeting  ",
            "  Review evidence  ",
            new DateTimeOffset(
                2026,
                9,
                1,
                9,
                0,
                0,
                TimeSpan.FromHours(-3)),
            new DateTimeOffset(
                2026,
                9,
                1,
                11,
                0,
                0,
                TimeSpan.FromHours(-3)),
            "  Conference room  ",
            clientId,
            processId,
            assigneeMembershipId);
    }

    private static CalendarEventCreationLockedState CreateLockedState(
        OrganizationRole role,
        Guid? assigneeMembershipId,
        bool organizationActive = true,
        bool actorMembershipActive = true,
        bool actorUserActive = true,
        bool assigneeAvailable = true,
        bool? isClientAvailable = null,
        bool? isProcessAvailable = null)
    {
        var actor = new CalendarEventMemberState(
            ActorMembershipId,
            OrganizationId,
            UserId,
            role,
            actorMembershipActive,
            actorUserActive);
        CalendarEventMemberState? assignee = assigneeMembershipId switch
        {
            null => null,
            _ when !assigneeAvailable => null,
            _ when assigneeMembershipId == ActorMembershipId => actor,
            _ => new CalendarEventMemberState(
                assigneeMembershipId.Value,
                OrganizationId,
                OtherUserId,
                OrganizationRole.Member,
                true,
                true)
        };

        return new CalendarEventCreationLockedState(
            organizationActive,
            actor,
            assignee,
            isClientAvailable,
            isProcessAvailable);
    }

    private static Guid? ResolveAssignee(AssignmentSelection selection)
    {
        return selection switch
        {
            AssignmentSelection.None => null,
            AssignmentSelection.Self => ActorMembershipId,
            AssignmentSelection.Other => OtherMembershipId,
            _ => throw new ArgumentOutOfRangeException(nameof(selection))
        };
    }

    public enum AssignmentSelection
    {
        None = 0,
        Self = 1,
        Other = 2
    }

    public enum AssociationSelection
    {
        General = 0,
        Client = 1,
        Process = 2
    }

    public enum RelatedResource
    {
        Client = 0,
        Process = 1
    }

    private sealed class StubAccessLookup(OrganizationRole? role)
        : IOrganizationAccessLookup
    {
        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException();
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            OrganizationAccessLookupResult? result = role.HasValue
                ? new OrganizationAccessLookupResult(
                    UserId,
                    OrganizationId,
                    ActorMembershipId,
                    role.Value)
                : null;
            return Task.FromResult(result);
        }
    }

    private sealed class StubClientLookup(bool exists)
        : IActiveClientInOrganizationLookup
    {
        public int CallCount { get; private set; }

        public Task<bool> ExistsAsync(
            Guid clientId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Assert.Equal(OrganizationId, organizationId);
            return Task.FromResult(exists);
        }
    }

    private sealed class StubProcessLookup(bool exists)
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

    private sealed class StubCreationPersistence(
        CalendarEventCreationLockedState state)
        : ICalendarEventCreationPersistence
    {
        public int CallCount { get; private set; }

        public CalendarEvent? CalendarEvent { get; private set; }

        public Task<CalendarEventCreationPersistenceResult> ExecuteAsync(
            CalendarEventCreationPersistenceRequest request,
            Func<CalendarEventCreationLockedState, CalendarEventCreationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CalendarEventCreationDecision decision = decide(state);
            CalendarEvent = decision.CalendarEvent;

            return Task.FromResult(
                decision.Status == CalendarEventCreationDecisionStatus.Persist
                    ? CalendarEventCreationPersistenceResult.Created(
                        Assert.IsType<CalendarEvent>(CalendarEvent).Id)
                    : CalendarEventCreationPersistenceResult.Rejected(
                        decision.Status));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
