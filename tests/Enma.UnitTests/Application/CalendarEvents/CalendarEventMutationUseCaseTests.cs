using Enma.Application.Authorization;
using Enma.Application.CalendarEvents;
using Enma.Application.CalendarEvents.Assignment;
using Enma.Application.CalendarEvents.Delete;
using Enma.Application.CalendarEvents.Update;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.CalendarEvents;

public sealed class CalendarEventMutationUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "01ef9a0b-b343-47a1-9515-6b1375858849");
    private static readonly Guid OrganizationId = Guid.Parse(
        "9820ce51-3ceb-4ba9-99ce-faf1f0497f2b");
    private static readonly Guid ActorMembershipId = Guid.Parse(
        "5211f37c-22a6-43e4-8d58-cef3c9b52a43");
    private static readonly Guid OtherMembershipId = Guid.Parse(
        "34d679cd-c244-4c5c-9d30-0397764bd1bc");
    private static readonly Guid OtherUserId = Guid.Parse(
        "266749e3-211a-4c30-924e-39a1eab301fb");
    private static readonly Guid ClientId = Guid.Parse(
        "68978591-d07f-4f38-81b4-0f2838310dd8");
    private static readonly Guid ProcessId = Guid.Parse(
        "549b625d-a9d0-4a1e-97d1-966e7c768788");
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse(
        "2026-08-22T14:00:00Z");
    private static readonly DateTimeOffset OriginalStartsAt = DateTimeOffset.Parse(
        "2026-09-01T12:00:00Z");
    private static readonly DateTimeOffset OriginalEndsAt = DateTimeOffset.Parse(
        "2026-09-01T13:00:00Z");

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task Update_PrivilegedRole_CanUpdateAnySameOrganizationEvent(
        OrganizationRole role)
    {
        CalendarEvent calendarEvent = CreateEvent(OtherMembershipId);
        var persistence = new StubMutationPersistence(calendarEvent, role);
        UpdateCalendarEventUseCase useCase = CreateUpdateUseCase(role, persistence);

        UpdateCalendarEventResult result = await useCase.ExecuteAsync(
            CreateUpdateCommand(calendarEvent.Id));

        Assert.Equal(UpdateCalendarEventResult.Succeeded, result);
        Assert.Equal("Updated hearing", calendarEvent.Title);
    }

    [Fact]
    public async Task Update_MemberCanUpdateOnlyEventTheyCreated()
    {
        CalendarEvent ownEvent = CreateEvent(ActorMembershipId);
        CalendarEvent otherEvent = CreateEvent(
            OtherMembershipId,
            assigneeMembershipId: ActorMembershipId);
        var ownPersistence = new StubMutationPersistence(
            ownEvent,
            OrganizationRole.Member);
        var otherPersistence = new StubMutationPersistence(
            otherEvent,
            OrganizationRole.Member);

        UpdateCalendarEventResult own = await CreateUpdateUseCase(
                OrganizationRole.Member,
                ownPersistence)
            .ExecuteAsync(CreateUpdateCommand(ownEvent.Id));
        UpdateCalendarEventResult other = await CreateUpdateUseCase(
                OrganizationRole.Member,
                otherPersistence)
            .ExecuteAsync(CreateUpdateCommand(otherEvent.Id));

        Assert.Equal(UpdateCalendarEventResult.Succeeded, own);
        Assert.Equal(UpdateCalendarEventResult.AccessDenied, other);
        Assert.Equal("Original event", otherEvent.Title);
    }

    [Theory]
    [InlineData(AssociationSelection.General)]
    [InlineData(AssociationSelection.Client)]
    [InlineData(AssociationSelection.Process)]
    public async Task Update_MutableFieldsAndAssociation_PreserveImmutableIdentity(
        AssociationSelection association)
    {
        CalendarEvent calendarEvent = CreateEvent(ActorMembershipId);
        Guid originalId = calendarEvent.Id;
        Guid originalOrganizationId = calendarEvent.OrganizationId;
        Guid originalCreatorId = calendarEvent.CreatedByMembershipId;
        DateTimeOffset originalCreatedAt = calendarEvent.CreatedAt;
        var persistence = new StubMutationPersistence(
            calendarEvent,
            OrganizationRole.Member);
        var startsAt = new DateTimeOffset(
            2026,
            10,
            2,
            8,
            30,
            0,
            TimeSpan.FromHours(-4));
        var endsAt = new DateTimeOffset(
            2026,
            10,
            2,
            15,
            0,
            0,
            TimeSpan.FromHours(2));
        UpdateCalendarEventCommand command = CreateUpdateCommand(calendarEvent.Id) with
        {
            StartsAt = startsAt,
            EndsAt = endsAt,
            ClientId = association == AssociationSelection.Client ? ClientId : null,
            ProcessId = association == AssociationSelection.Process ? ProcessId : null
        };

        UpdateCalendarEventResult result = await CreateUpdateUseCase(
                OrganizationRole.Member,
                persistence)
            .ExecuteAsync(command);

        Assert.Equal(UpdateCalendarEventResult.Succeeded, result);
        Assert.Equal("Updated hearing", calendarEvent.Title);
        Assert.Equal("Updated description", calendarEvent.Description);
        Assert.Equal("Courtroom 4", calendarEvent.Location);
        Assert.Equal(startsAt.UtcDateTime, calendarEvent.StartsAt.UtcDateTime);
        Assert.Equal(endsAt.UtcDateTime, calendarEvent.EndsAt.UtcDateTime);
        Assert.Equal(
            association == AssociationSelection.Client ? ClientId : null,
            calendarEvent.ClientId);
        Assert.Equal(
            association == AssociationSelection.Process ? ProcessId : null,
            calendarEvent.ProcessId);
        Assert.Equal(originalId, calendarEvent.Id);
        Assert.Equal(originalOrganizationId, calendarEvent.OrganizationId);
        Assert.Equal(originalCreatorId, calendarEvent.CreatedByMembershipId);
        Assert.Equal(originalCreatedAt, calendarEvent.CreatedAt);
    }

    [Theory]
    [InlineData(AssociationSelection.Client)]
    [InlineData(AssociationSelection.Process)]
    public async Task Update_UnavailableOrCrossTenantAssociation_IsRejected(
        AssociationSelection association)
    {
        CalendarEvent calendarEvent = CreateEvent(ActorMembershipId);
        var persistence = new StubMutationPersistence(
            calendarEvent,
            OrganizationRole.Member,
            associationAvailable: false);
        UpdateCalendarEventCommand command = CreateUpdateCommand(calendarEvent.Id) with
        {
            ClientId = association == AssociationSelection.Client ? ClientId : null,
            ProcessId = association == AssociationSelection.Process ? ProcessId : null
        };

        UpdateCalendarEventResult result = await CreateUpdateUseCase(
                OrganizationRole.Member,
                persistence)
            .ExecuteAsync(command);

        Assert.Equal(
            association == AssociationSelection.Client
                ? UpdateCalendarEventResult.RelatedClientUnavailable
                : UpdateCalendarEventResult.RelatedProcessUnavailable,
            result);
        Assert.Equal("Original event", calendarEvent.Title);
    }

    [Fact]
    public async Task Update_InactiveLockedActorOrOrganization_DeniesWithoutMutation()
    {
        CalendarEvent calendarEvent = CreateEvent(ActorMembershipId);
        var persistence = new StubMutationPersistence(
            calendarEvent,
            OrganizationRole.Member,
            organizationActive: false);

        UpdateCalendarEventResult result = await CreateUpdateUseCase(
                OrganizationRole.Member,
                persistence)
            .ExecuteAsync(CreateUpdateCommand(calendarEvent.Id));

        Assert.Equal(UpdateCalendarEventResult.AccessDenied, result);
        Assert.Equal("Original event", calendarEvent.Title);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ChangeAssignee_PrivilegedRole_CanAssignOtherOrClear(
        OrganizationRole role)
    {
        CalendarEvent calendarEvent = CreateEvent(
            OtherMembershipId,
            assigneeMembershipId: null);
        var persistence = new StubMutationPersistence(calendarEvent, role);
        ChangeCalendarEventAssigneeUseCase useCase = CreateAssigneeUseCase(
            role,
            persistence);

        ChangeCalendarEventAssigneeResult assign = await useCase.ExecuteAsync(
            new ChangeCalendarEventAssigneeCommand(
                UserId,
                OrganizationId,
                calendarEvent.Id,
                OtherMembershipId));
        ChangeCalendarEventAssigneeResult clear = await useCase.ExecuteAsync(
            new ChangeCalendarEventAssigneeCommand(
                UserId,
                OrganizationId,
                calendarEvent.Id,
                null));

        Assert.Equal(ChangeCalendarEventAssigneeResult.Succeeded, assign);
        Assert.Equal(ChangeCalendarEventAssigneeResult.Succeeded, clear);
        Assert.Null(calendarEvent.AssigneeMembershipId);
    }

    [Fact]
    public async Task ChangeAssignee_MemberOwnEvent_CanAssignSelfAndClear()
    {
        CalendarEvent calendarEvent = CreateEvent(ActorMembershipId);
        var persistence = new StubMutationPersistence(
            calendarEvent,
            OrganizationRole.Member);
        ChangeCalendarEventAssigneeUseCase useCase = CreateAssigneeUseCase(
            OrganizationRole.Member,
            persistence);

        ChangeCalendarEventAssigneeResult assign = await useCase.ExecuteAsync(
            new ChangeCalendarEventAssigneeCommand(
                UserId,
                OrganizationId,
                calendarEvent.Id,
                ActorMembershipId));
        ChangeCalendarEventAssigneeResult clear = await useCase.ExecuteAsync(
            new ChangeCalendarEventAssigneeCommand(
                UserId,
                OrganizationId,
                calendarEvent.Id,
                null));

        Assert.Equal(ChangeCalendarEventAssigneeResult.Succeeded, assign);
        Assert.Equal(ChangeCalendarEventAssigneeResult.Succeeded, clear);
        Assert.Null(calendarEvent.AssigneeMembershipId);
    }

    [Fact]
    public async Task ChangeAssignee_MemberCannotAssignOtherOrMutateAnotherCreatorsEvent()
    {
        CalendarEvent ownEvent = CreateEvent(ActorMembershipId);
        CalendarEvent otherEvent = CreateEvent(OtherMembershipId);
        var ownPersistence = new StubMutationPersistence(
            ownEvent,
            OrganizationRole.Member);
        var otherPersistence = new StubMutationPersistence(
            otherEvent,
            OrganizationRole.Member);

        ChangeCalendarEventAssigneeResult assignOther =
            await CreateAssigneeUseCase(
                    OrganizationRole.Member,
                    ownPersistence)
                .ExecuteAsync(new ChangeCalendarEventAssigneeCommand(
                    UserId,
                    OrganizationId,
                    ownEvent.Id,
                    OtherMembershipId));
        ChangeCalendarEventAssigneeResult claimOtherEvent =
            await CreateAssigneeUseCase(
                    OrganizationRole.Member,
                    otherPersistence)
                .ExecuteAsync(new ChangeCalendarEventAssigneeCommand(
                    UserId,
                    OrganizationId,
                    otherEvent.Id,
                    ActorMembershipId));

        Assert.Equal(ChangeCalendarEventAssigneeResult.AccessDenied, assignOther);
        Assert.Equal(ChangeCalendarEventAssigneeResult.AccessDenied, claimOtherEvent);
        Assert.Null(ownEvent.AssigneeMembershipId);
        Assert.Null(otherEvent.AssigneeMembershipId);
    }

    [Fact]
    public async Task ChangeAssignee_UnavailableTarget_ReturnsControlledResult()
    {
        CalendarEvent calendarEvent = CreateEvent(ActorMembershipId);
        var persistence = new StubMutationPersistence(
            calendarEvent,
            OrganizationRole.Member,
            assigneeAvailable: false);

        ChangeCalendarEventAssigneeResult result =
            await CreateAssigneeUseCase(
                    OrganizationRole.Member,
                    persistence)
                .ExecuteAsync(new ChangeCalendarEventAssigneeCommand(
                    UserId,
                    OrganizationId,
                    calendarEvent.Id,
                    ActorMembershipId));

        Assert.Equal(
            ChangeCalendarEventAssigneeResult.RelatedAssigneeUnavailable,
            result);
        Assert.Null(calendarEvent.AssigneeMembershipId);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner, false)]
    [InlineData(OrganizationRole.Administrator, false)]
    [InlineData(OrganizationRole.Member, true)]
    [InlineData(OrganizationRole.Member, false)]
    public async Task Delete_EnforcesCreatorContract(
        OrganizationRole role,
        bool ownEvent)
    {
        CalendarEvent calendarEvent = CreateEvent(
            ownEvent ? ActorMembershipId : OtherMembershipId);
        var persistence = new StubMutationPersistence(calendarEvent, role);
        DeleteCalendarEventUseCase useCase = CreateDeleteUseCase(role, persistence);

        DeleteCalendarEventResult result = await useCase.ExecuteAsync(
            new DeleteCalendarEventCommand(
                UserId,
                OrganizationId,
                calendarEvent.Id));

        bool expectedDelete = role is OrganizationRole.Owner or
            OrganizationRole.Administrator || ownEvent;
        Assert.Equal(
            expectedDelete
                ? DeleteCalendarEventResult.Deleted
                : DeleteCalendarEventResult.AccessDenied,
            result);
        Assert.Equal(expectedDelete, persistence.Deleted);
    }

    [Fact]
    public async Task ForeignOrMissingEvent_IsNotFoundForEveryMutation()
    {
        CalendarEvent calendarEvent = CreateEvent(ActorMembershipId);
        var persistence = new StubMutationPersistence(
            calendarEvent,
            OrganizationRole.Member,
            eventExists: false);

        UpdateCalendarEventResult update = await CreateUpdateUseCase(
                OrganizationRole.Member,
                persistence)
            .ExecuteAsync(CreateUpdateCommand(calendarEvent.Id));
        ChangeCalendarEventAssigneeResult assignment =
            await CreateAssigneeUseCase(
                    OrganizationRole.Member,
                    persistence)
                .ExecuteAsync(new ChangeCalendarEventAssigneeCommand(
                    UserId,
                    OrganizationId,
                    calendarEvent.Id,
                    null));
        DeleteCalendarEventResult delete = await CreateDeleteUseCase(
                OrganizationRole.Member,
                persistence)
            .ExecuteAsync(new DeleteCalendarEventCommand(
                UserId,
                OrganizationId,
                calendarEvent.Id));

        Assert.Equal(UpdateCalendarEventResult.NotFound, update);
        Assert.Equal(ChangeCalendarEventAssigneeResult.NotFound, assignment);
        Assert.Equal(DeleteCalendarEventResult.NotFound, delete);
    }

    private static UpdateCalendarEventUseCase CreateUpdateUseCase(
        OrganizationRole role,
        ICalendarEventMutationPersistence persistence)
    {
        return new UpdateCalendarEventUseCase(
            CreateAccessAuthorization(role),
            new CalendarEventActionAuthorization(),
            persistence);
    }

    private static ChangeCalendarEventAssigneeUseCase CreateAssigneeUseCase(
        OrganizationRole role,
        ICalendarEventMutationPersistence persistence)
    {
        return new ChangeCalendarEventAssigneeUseCase(
            CreateAccessAuthorization(role),
            new CalendarEventActionAuthorization(),
            persistence);
    }

    private static DeleteCalendarEventUseCase CreateDeleteUseCase(
        OrganizationRole role,
        ICalendarEventMutationPersistence persistence)
    {
        return new DeleteCalendarEventUseCase(
            CreateAccessAuthorization(role),
            new CalendarEventActionAuthorization(),
            persistence);
    }

    private static CalendarEventAccessAuthorization CreateAccessAuthorization(
        OrganizationRole role)
    {
        return new CalendarEventAccessAuthorization(
            new OrganizationAccessAuthorization(new StubAccessLookup(role)));
    }

    private static UpdateCalendarEventCommand CreateUpdateCommand(Guid eventId)
    {
        return new UpdateCalendarEventCommand(
            UserId,
            OrganizationId,
            eventId,
            "  Updated hearing  ",
            "  Updated description  ",
            DateTimeOffset.Parse("2026-10-01T15:00:00Z"),
            DateTimeOffset.Parse("2026-10-01T16:30:00Z"),
            "  Courtroom 4  ",
            null,
            null);
    }

    private static CalendarEvent CreateEvent(
        Guid creatorMembershipId,
        Guid? assigneeMembershipId = null)
    {
        return new CalendarEvent(
            OrganizationId,
            "Original event",
            "Original description",
            OriginalStartsAt,
            OriginalEndsAt,
            "Original location",
            null,
            null,
            assigneeMembershipId,
            creatorMembershipId,
            CreatedAt);
    }

    public enum AssociationSelection
    {
        General = 0,
        Client = 1,
        Process = 2
    }

    private sealed class StubAccessLookup(OrganizationRole role)
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
            return Task.FromResult<OrganizationAccessLookupResult?>(
                new OrganizationAccessLookupResult(
                    UserId,
                    OrganizationId,
                    ActorMembershipId,
                    role));
        }
    }

    private sealed class StubMutationPersistence(
        CalendarEvent calendarEvent,
        OrganizationRole lockedRole,
        bool organizationActive = true,
        bool actorAvailable = true,
        bool associationAvailable = true,
        bool assigneeAvailable = true,
        bool eventExists = true) : ICalendarEventMutationPersistence
    {
        public bool Deleted { get; private set; }

        public Task<CalendarEventMutationPersistenceResult> ExecuteAsync(
            CalendarEventMutationPersistenceRequest request,
            Func<CalendarEventMutationLockedState, CalendarEventMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            if (!eventExists)
            {
                return Task.FromResult(
                    CalendarEventMutationPersistenceResult.NotFound);
            }

            CalendarEventMemberState? actor = actorAvailable
                ? new CalendarEventMemberState(
                    ActorMembershipId,
                    OrganizationId,
                    UserId,
                    lockedRole,
                    true,
                    true)
                : null;
            var state = new CalendarEventMutationLockedState(
                calendarEvent,
                organizationActive,
                actor,
                false,
                null,
                null,
                null,
                null,
                false,
                null,
                null);
            CalendarEventMutationDecision decision = decide(state);

            if (decision.Status ==
                CalendarEventMutationDecisionStatus.ValidateAssociation)
            {
                state = state with
                {
                    AssociationLookupPerformed = true,
                    ValidatedClientId = decision.ClientId,
                    IsClientAvailable = decision.ClientId is null
                        ? null
                        : associationAvailable,
                    ValidatedProcessId = decision.ProcessId,
                    IsProcessAvailable = decision.ProcessId is null
                        ? null
                        : associationAvailable
                };
                decision = decide(state);
            }

            if (decision.Status ==
                CalendarEventMutationDecisionStatus.ValidateAssignee)
            {
                Guid requestedId = Assert.IsType<Guid>(
                    decision.AssigneeMembershipId);
                state = state with
                {
                    AssigneeLookupPerformed = true,
                    ValidatedAssigneeMembershipId = requestedId,
                    Assignee = assigneeAvailable
                        ? new CalendarEventMemberState(
                            requestedId,
                            OrganizationId,
                            requestedId == ActorMembershipId
                                ? UserId
                                : OtherUserId,
                            OrganizationRole.Member,
                            true,
                            true)
                        : null
                };
                decision = decide(state);
            }

            CalendarEventMutationPersistenceResult result = decision.Status switch
            {
                CalendarEventMutationDecisionStatus.AccessDenied =>
                    CalendarEventMutationPersistenceResult.AccessDenied,
                CalendarEventMutationDecisionStatus.RelatedClientUnavailable =>
                    CalendarEventMutationPersistenceResult.RelatedClientUnavailable,
                CalendarEventMutationDecisionStatus.RelatedProcessUnavailable =>
                    CalendarEventMutationPersistenceResult.RelatedProcessUnavailable,
                CalendarEventMutationDecisionStatus.RelatedAssigneeUnavailable =>
                    CalendarEventMutationPersistenceResult.RelatedAssigneeUnavailable,
                CalendarEventMutationDecisionStatus.InvalidInput =>
                    CalendarEventMutationPersistenceResult.InvalidInput,
                CalendarEventMutationDecisionStatus.Persist =>
                    CalendarEventMutationPersistenceResult.Succeeded,
                CalendarEventMutationDecisionStatus.Delete =>
                    CalendarEventMutationPersistenceResult.Deleted,
                _ => throw new InvalidOperationException()
            };

            Deleted = result == CalendarEventMutationPersistenceResult.Deleted;
            return Task.FromResult(result);
        }
    }
}
