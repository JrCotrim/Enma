using Enma.Application.Authorization;
using Enma.Application.CalendarEvents;
using Enma.Application.CalendarEvents.Assignment;
using Enma.Application.CalendarEvents.Create;
using Enma.Application.CalendarEvents.Delete;
using Enma.Application.CalendarEvents.GetById;
using Enma.Application.CalendarEvents.Update;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Application.CalendarEvents;

[Collection(PostgreSqlCollection.Name)]
public sealed class CalendarEventUseCasesPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset SeededAt = DateTimeOffset.Parse(
        "2026-08-22T12:00:00Z");
    private static readonly DateTimeOffset EventCreatedAt = DateTimeOffset.Parse(
        "2026-08-22T15:00:00Z");
    private static readonly DateTimeOffset StartsAt = DateTimeOffset.Parse(
        "2026-09-01T12:00:00Z");
    private static readonly DateTimeOffset EndsAt = DateTimeOffset.Parse(
        "2026-09-01T13:00:00Z");

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [MemberData(nameof(CreateMatrix))]
    public async Task Create_RoleAssociationAndAssignmentMatrix_PersistsUtc(
        OrganizationRole role,
        AssociationSelection association,
        AssignmentSelection assignment)
    {
        TenantGraph graph = await SeedTenantAsync(role);
        Guid? clientId = association == AssociationSelection.Client
            ? graph.Client.Id
            : null;
        Guid? processId = association == AssociationSelection.Process
            ? graph.Process.Id
            : null;
        Guid? assigneeId = assignment switch
        {
            AssignmentSelection.None => null,
            AssignmentSelection.Self => graph.ActorMembership.Id,
            AssignmentSelection.Other => graph.OtherMembership.Id,
            _ => throw new ArgumentOutOfRangeException(nameof(assignment))
        };
        var startsAt = new DateTimeOffset(
            2026,
            9,
            1,
            9,
            0,
            0,
            TimeSpan.FromHours(-3));
        var endsAt = new DateTimeOffset(
            2026,
            9,
            1,
            15,
            30,
            0,
            TimeSpan.FromHours(2));
        await using EnmaDbContext queryContext = fixture.CreateDbContext();

        CreateCalendarEventResult result = await CreateCreateUseCase(queryContext)
            .ExecuteAsync(new CreateCalendarEventCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                "  Strategy meeting  ",
                "  Review evidence  ",
                startsAt,
                endsAt,
                "  Main office  ",
                clientId,
                processId,
                assigneeId));

        Assert.Equal(CreateCalendarEventResultStatus.Created, result.Status);
        CalendarEvent persisted = await FindEventAsync(AssertEventId(result));
        Assert.Equal(graph.Organization.Id, persisted.OrganizationId);
        Assert.Equal(graph.ActorMembership.Id, persisted.CreatedByMembershipId);
        Assert.Equal(assigneeId, persisted.AssigneeMembershipId);
        Assert.Equal(clientId, persisted.ClientId);
        Assert.Equal(processId, persisted.ProcessId);
        Assert.Equal(startsAt.UtcDateTime, persisted.StartsAt.UtcDateTime);
        Assert.Equal(endsAt.UtcDateTime, persisted.EndsAt.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, persisted.StartsAt.Offset);
        Assert.Equal(TimeSpan.Zero, persisted.EndsAt.Offset);
        Assert.Equal(EventCreatedAt, persisted.CreatedAt);
    }

    [Fact]
    public async Task Create_MemberCannotAssignAnotherMembership()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Member);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();

        CreateCalendarEventResult result = await CreateCreateUseCase(queryContext)
            .ExecuteAsync(CreateCommand(
                graph,
                assigneeMembershipId: graph.OtherMembership.Id));

        Assert.Same(CreateCalendarEventResult.AccessDenied, result);
        Assert.Equal(0, await CountEventsAsync());
    }

    [Fact]
    public async Task Create_CrossTenantRelationsAndAssignee_AreIndistinguishableFromMissing()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        TenantGraph other = await SeedTenantAsync(
            OrganizationRole.Owner,
            suffix: "other");
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        CreateCalendarEventUseCase useCase = CreateCreateUseCase(queryContext);

        CreateCalendarEventResult crossClient = await useCase.ExecuteAsync(
            CreateCommand(graph, clientId: other.Client.Id));
        CreateCalendarEventResult missingClient = await useCase.ExecuteAsync(
            CreateCommand(graph, clientId: Guid.NewGuid()));
        CreateCalendarEventResult crossProcess = await useCase.ExecuteAsync(
            CreateCommand(graph, processId: other.Process.Id));
        CreateCalendarEventResult missingProcess = await useCase.ExecuteAsync(
            CreateCommand(graph, processId: Guid.NewGuid()));
        CreateCalendarEventResult crossAssignee = await useCase.ExecuteAsync(
            CreateCommand(
                graph,
                assigneeMembershipId: other.OtherMembership.Id));
        CreateCalendarEventResult missingAssignee = await useCase.ExecuteAsync(
            CreateCommand(graph, assigneeMembershipId: Guid.NewGuid()));

        Assert.Same(CreateCalendarEventResult.RelatedClientUnavailable, crossClient);
        Assert.Same(crossClient, missingClient);
        Assert.Same(CreateCalendarEventResult.RelatedProcessUnavailable, crossProcess);
        Assert.Same(crossProcess, missingProcess);
        Assert.Same(
            CreateCalendarEventResult.RelatedAssigneeUnavailable,
            crossAssignee);
        Assert.Same(crossAssignee, missingAssignee);
        Assert.Equal(0, await CountEventsAsync());
    }

    [Fact]
    public async Task Create_InactiveClientAndInactiveAssignee_AreUnavailable()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        await DeactivateClientAsync(graph.Client.Id);
        await DeactivateMembershipAsync(graph.OtherMembership.Id);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        CreateCalendarEventUseCase useCase = CreateCreateUseCase(queryContext);

        CreateCalendarEventResult client = await useCase.ExecuteAsync(
            CreateCommand(graph, clientId: graph.Client.Id));
        CreateCalendarEventResult assignee = await useCase.ExecuteAsync(
            CreateCommand(
                graph,
                assigneeMembershipId: graph.OtherMembership.Id));

        Assert.Same(CreateCalendarEventResult.RelatedClientUnavailable, client);
        Assert.Same(
            CreateCalendarEventResult.RelatedAssigneeUnavailable,
            assignee);
        Assert.Equal(0, await CountEventsAsync());
    }

    [Fact]
    public async Task Create_ClientDeactivatedAfterPrecheck_IsRejectedByLockedState()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        var persistence = new BeforeCreationPersistence(
            new CalendarEventCreationPersistence(CreateOptions()),
            async () => await DeactivateClientAsync(graph.Client.Id));
        var useCase = new CreateCalendarEventUseCase(
            CreateAccessAuthorization(queryContext),
            new CalendarEventActionAuthorization(),
            new ActiveClientInOrganizationLookup(queryContext),
            new ProcessOrganizationOwnershipLookup(queryContext),
            persistence,
            new FixedTimeProvider(EventCreatedAt));

        CreateCalendarEventResult result = await useCase.ExecuteAsync(
            CreateCommand(graph, clientId: graph.Client.Id));

        Assert.Same(CreateCalendarEventResult.RelatedClientUnavailable, result);
        Assert.Equal(0, await CountEventsAsync());
    }

    [Fact]
    public async Task Create_AdministratorDemotedAfterPrecheck_CannotAssignOtherMember()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Administrator);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        var persistence = new BeforeCreationPersistence(
            new CalendarEventCreationPersistence(CreateOptions()),
            async () => await ChangeRoleAsync(
                graph.ActorMembership.Id,
                OrganizationRole.Member));
        var useCase = new CreateCalendarEventUseCase(
            CreateAccessAuthorization(queryContext),
            new CalendarEventActionAuthorization(),
            new ActiveClientInOrganizationLookup(queryContext),
            new ProcessOrganizationOwnershipLookup(queryContext),
            persistence,
            new FixedTimeProvider(EventCreatedAt));

        CreateCalendarEventResult result = await useCase.ExecuteAsync(
            CreateCommand(
                graph,
                assigneeMembershipId: graph.OtherMembership.Id));

        Assert.Same(CreateCalendarEventResult.AccessDenied, result);
        Assert.Equal(0, await CountEventsAsync());
    }

    [Theory]
    [InlineData(InactiveActorState.Organization)]
    [InlineData(InactiveActorState.Membership)]
    [InlineData(InactiveActorState.User)]
    public async Task Create_InactiveLiveActorState_Denies(
        InactiveActorState inactiveState)
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);

        switch (inactiveState)
        {
            case InactiveActorState.Organization:
                await DeactivateOrganizationAsync(graph.Organization.Id);
                break;
            case InactiveActorState.Membership:
                await DeactivateMembershipAsync(graph.ActorMembership.Id);
                break;
            case InactiveActorState.User:
                await DeactivateUserAsync(graph.ActorUser.Id);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(inactiveState));
        }

        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        CreateCalendarEventResult result = await CreateCreateUseCase(queryContext)
            .ExecuteAsync(CreateCommand(graph));

        Assert.Same(CreateCalendarEventResult.AccessDenied, result);
        Assert.Equal(0, await CountEventsAsync());
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task Get_AllRolesReadMinimalDetail(OrganizationRole role)
    {
        TenantGraph graph = await SeedTenantAsync(role);
        CalendarEvent calendarEvent = new(
            graph.Organization.Id,
            "Client conference",
            "Discuss settlement",
            StartsAt,
            EndsAt,
            "Room 8",
            graph.Client.Id,
            null,
            graph.OtherMembership.Id,
            graph.ActorMembership.Id,
            EventCreatedAt);
        await SeedAsync(calendarEvent);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();

        GetCalendarEventResult result = await CreateGetUseCase(queryContext)
            .ExecuteAsync(new GetCalendarEventQuery(
                graph.ActorUser.Id,
                graph.Organization.Id,
                calendarEvent.Id));

        Assert.Equal(GetCalendarEventResultStatus.Succeeded, result.Status);
        CalendarEventDetailReadModel detail = Assert.IsType<
            CalendarEventDetailReadModel>(result.CalendarEvent);
        Assert.Equal(calendarEvent.Id, detail.Id);
        Assert.Equal(graph.Client.Id, detail.ClientId);
        Assert.Equal(graph.Client.Name, detail.ClientName);
        Assert.Null(detail.ProcessId);
        Assert.Null(detail.ProcessTitle);
        Assert.Equal(graph.OtherMembership.Id, detail.AssigneeMembershipId);
        Assert.Equal(graph.OtherUser.Name, detail.AssigneeDisplayName);
        Assert.Equal(graph.ActorMembership.Id, detail.CreatedByMembershipId);
        Assert.Equal(graph.ActorUser.Name, detail.CreatedByDisplayName);
        Assert.Equal(EventCreatedAt, detail.CreatedAt);
    }

    [Fact]
    public async Task Get_ProcessEventProjectsProcessAndForeignOrMissingIsNotFound()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Member);
        TenantGraph other = await SeedTenantAsync(
            OrganizationRole.Owner,
            suffix: "foreign-read");
        CalendarEvent ownEvent = CreateEvent(
            graph,
            graph.ActorMembership.Id,
            processId: graph.Process.Id);
        CalendarEvent foreignEvent = CreateEvent(
            other,
            other.ActorMembership.Id);
        await SeedAsync(ownEvent, foreignEvent);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        GetCalendarEventUseCase useCase = CreateGetUseCase(queryContext);

        GetCalendarEventResult own = await useCase.ExecuteAsync(
            new GetCalendarEventQuery(
                graph.ActorUser.Id,
                graph.Organization.Id,
                ownEvent.Id));
        GetCalendarEventResult foreign = await useCase.ExecuteAsync(
            new GetCalendarEventQuery(
                graph.ActorUser.Id,
                graph.Organization.Id,
                foreignEvent.Id));
        GetCalendarEventResult missing = await useCase.ExecuteAsync(
            new GetCalendarEventQuery(
                graph.ActorUser.Id,
                graph.Organization.Id,
                Guid.NewGuid()));

        Assert.Equal(graph.Process.Id, own.CalendarEvent?.ProcessId);
        Assert.Equal(graph.Process.Title, own.CalendarEvent?.ProcessTitle);
        Assert.Same(GetCalendarEventResult.NotFound, foreign);
        Assert.Same(foreign, missing);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner, false, true)]
    [InlineData(OrganizationRole.Administrator, false, true)]
    [InlineData(OrganizationRole.Member, true, true)]
    [InlineData(OrganizationRole.Member, false, false)]
    public async Task Update_AuthorizationMatrix_UsesCreatorNotAssignee(
        OrganizationRole role,
        bool actorCreated,
        bool expectedSuccess)
    {
        TenantGraph graph = await SeedTenantAsync(role);
        CalendarEvent calendarEvent = CreateEvent(
            graph,
            actorCreated
                ? graph.ActorMembership.Id
                : graph.OtherMembership.Id,
            assigneeMembershipId: graph.ActorMembership.Id);
        await SeedAsync(calendarEvent);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();

        UpdateCalendarEventResult result = await CreateUpdateUseCase(queryContext)
            .ExecuteAsync(CreateUpdateCommand(graph, calendarEvent.Id));

        Assert.Equal(
            expectedSuccess
                ? UpdateCalendarEventResult.Succeeded
                : UpdateCalendarEventResult.AccessDenied,
            result);
        CalendarEvent persisted = await FindEventAsync(calendarEvent.Id);
        Assert.Equal(
            expectedSuccess ? "Updated hearing" : "Original event",
            persisted.Title);
    }

    [Fact]
    public async Task Update_DetailsTimeAndAssociationTransitions_PreserveImmutableFields()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Member);
        CalendarEvent calendarEvent = CreateEvent(
            graph,
            graph.ActorMembership.Id);
        await SeedAsync(calendarEvent);
        Guid originalId = calendarEvent.Id;
        Guid originalOrganizationId = calendarEvent.OrganizationId;
        Guid originalCreatorId = calendarEvent.CreatedByMembershipId;
        DateTimeOffset originalCreatedAt = calendarEvent.CreatedAt;
        var offsetStart = new DateTimeOffset(
            2026,
            10,
            1,
            8,
            0,
            0,
            TimeSpan.FromHours(-4));
        var offsetEnd = new DateTimeOffset(
            2026,
            10,
            1,
            15,
            30,
            0,
            TimeSpan.FromHours(2));
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        UpdateCalendarEventUseCase useCase = CreateUpdateUseCase(queryContext);

        UpdateCalendarEventResult directClient = await useCase.ExecuteAsync(
            CreateUpdateCommand(graph, calendarEvent.Id) with
            {
                StartsAt = offsetStart,
                EndsAt = offsetEnd,
                ClientId = graph.Client.Id
            });
        CalendarEvent offsetPersisted = await FindEventAsync(calendarEvent.Id);
        UpdateCalendarEventResult process = await useCase.ExecuteAsync(
            CreateUpdateCommand(graph, calendarEvent.Id) with
            {
                ProcessId = graph.Process.Id
            });
        UpdateCalendarEventResult general = await useCase.ExecuteAsync(
            CreateUpdateCommand(graph, calendarEvent.Id));

        Assert.All(
            new[] { directClient, process, general },
            result => Assert.Equal(UpdateCalendarEventResult.Succeeded, result));
        Assert.Equal(
            offsetStart.UtcDateTime,
            offsetPersisted.StartsAt.UtcDateTime);
        Assert.Equal(offsetEnd.UtcDateTime, offsetPersisted.EndsAt.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, offsetPersisted.StartsAt.Offset);
        Assert.Equal(TimeSpan.Zero, offsetPersisted.EndsAt.Offset);
        CalendarEvent persisted = await FindEventAsync(calendarEvent.Id);
        Assert.Equal("Updated hearing", persisted.Title);
        Assert.Equal("Updated description", persisted.Description);
        Assert.Equal("Courtroom 4", persisted.Location);
        Assert.Equal(StartsAt, persisted.StartsAt);
        Assert.Equal(EndsAt, persisted.EndsAt);
        Assert.Null(persisted.ClientId);
        Assert.Null(persisted.ProcessId);
        Assert.Equal(originalId, persisted.Id);
        Assert.Equal(originalOrganizationId, persisted.OrganizationId);
        Assert.Equal(originalCreatorId, persisted.CreatedByMembershipId);
        Assert.Equal(originalCreatedAt, persisted.CreatedAt);
    }

    [Fact]
    public async Task Update_CrossTenantRelationsRejectedAndEventUnchanged()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        TenantGraph other = await SeedTenantAsync(
            OrganizationRole.Owner,
            suffix: "update-cross");
        CalendarEvent calendarEvent = CreateEvent(
            graph,
            graph.ActorMembership.Id);
        await SeedAsync(calendarEvent);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        UpdateCalendarEventUseCase useCase = CreateUpdateUseCase(queryContext);

        UpdateCalendarEventResult client = await useCase.ExecuteAsync(
            CreateUpdateCommand(graph, calendarEvent.Id) with
            {
                ClientId = other.Client.Id
            });
        UpdateCalendarEventResult process = await useCase.ExecuteAsync(
            CreateUpdateCommand(graph, calendarEvent.Id) with
            {
                ProcessId = other.Process.Id
            });

        Assert.Equal(UpdateCalendarEventResult.RelatedClientUnavailable, client);
        Assert.Equal(UpdateCalendarEventResult.RelatedProcessUnavailable, process);
        CalendarEvent persisted = await FindEventAsync(calendarEvent.Id);
        Assert.Equal("Original event", persisted.Title);
        Assert.Null(persisted.ClientId);
        Assert.Null(persisted.ProcessId);
    }

    [Fact]
    public async Task Assignment_PrivilegedAndMemberContracts_AreEnforced()
    {
        TenantGraph owner = await SeedTenantAsync(OrganizationRole.Owner);
        CalendarEvent privilegedEvent = CreateEvent(
            owner,
            owner.OtherMembership.Id);
        await SeedAsync(privilegedEvent);
        await using EnmaDbContext ownerContext = fixture.CreateDbContext();
        ChangeCalendarEventAssigneeUseCase ownerUseCase =
            CreateAssigneeUseCase(ownerContext);

        ChangeCalendarEventAssigneeResult assignOther =
            await ownerUseCase.ExecuteAsync(new ChangeCalendarEventAssigneeCommand(
                owner.ActorUser.Id,
                owner.Organization.Id,
                privilegedEvent.Id,
                owner.OtherMembership.Id));
        ChangeCalendarEventAssigneeResult clearOther =
            await ownerUseCase.ExecuteAsync(new ChangeCalendarEventAssigneeCommand(
                owner.ActorUser.Id,
                owner.Organization.Id,
                privilegedEvent.Id,
                null));

        Assert.Equal(ChangeCalendarEventAssigneeResult.Succeeded, assignOther);
        Assert.Equal(ChangeCalendarEventAssigneeResult.Succeeded, clearOther);

        await fixture.ResetDatabaseAsync();
        TenantGraph member = await SeedTenantAsync(OrganizationRole.Member);
        CalendarEvent ownEvent = CreateEvent(
            member,
            member.ActorMembership.Id);
        CalendarEvent otherEvent = CreateEvent(
            member,
            member.OtherMembership.Id,
            assigneeMembershipId: member.ActorMembership.Id);
        await SeedAsync(ownEvent, otherEvent);
        await using EnmaDbContext memberContext = fixture.CreateDbContext();
        ChangeCalendarEventAssigneeUseCase memberUseCase =
            CreateAssigneeUseCase(memberContext);

        ChangeCalendarEventAssigneeResult self =
            await memberUseCase.ExecuteAsync(new ChangeCalendarEventAssigneeCommand(
                member.ActorUser.Id,
                member.Organization.Id,
                ownEvent.Id,
                member.ActorMembership.Id));
        ChangeCalendarEventAssigneeResult clear =
            await memberUseCase.ExecuteAsync(new ChangeCalendarEventAssigneeCommand(
                member.ActorUser.Id,
                member.Organization.Id,
                ownEvent.Id,
                null));
        ChangeCalendarEventAssigneeResult assignAnother =
            await memberUseCase.ExecuteAsync(new ChangeCalendarEventAssigneeCommand(
                member.ActorUser.Id,
                member.Organization.Id,
                ownEvent.Id,
                member.OtherMembership.Id));
        ChangeCalendarEventAssigneeResult mutateOther =
            await memberUseCase.ExecuteAsync(new ChangeCalendarEventAssigneeCommand(
                member.ActorUser.Id,
                member.Organization.Id,
                otherEvent.Id,
                null));

        Assert.Equal(ChangeCalendarEventAssigneeResult.Succeeded, self);
        Assert.Equal(ChangeCalendarEventAssigneeResult.Succeeded, clear);
        Assert.Equal(ChangeCalendarEventAssigneeResult.AccessDenied, assignAnother);
        Assert.Equal(ChangeCalendarEventAssigneeResult.AccessDenied, mutateOther);
        Assert.Null((await FindEventAsync(ownEvent.Id)).AssigneeMembershipId);
        Assert.Equal(
            member.ActorMembership.Id,
            (await FindEventAsync(otherEvent.Id)).AssigneeMembershipId);
    }

    [Fact]
    public async Task Assignment_MissingCrossTenantAndInactiveTargets_AreUnavailable()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        TenantGraph other = await SeedTenantAsync(
            OrganizationRole.Owner,
            suffix: "assignee-cross");
        CalendarEvent calendarEvent = CreateEvent(
            graph,
            graph.ActorMembership.Id);
        await SeedAsync(calendarEvent);
        await DeactivateMembershipAsync(graph.OtherMembership.Id);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        ChangeCalendarEventAssigneeUseCase useCase =
            CreateAssigneeUseCase(queryContext);

        ChangeCalendarEventAssigneeResult missing = await useCase.ExecuteAsync(
            new ChangeCalendarEventAssigneeCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                calendarEvent.Id,
                Guid.NewGuid()));
        ChangeCalendarEventAssigneeResult crossTenant = await useCase.ExecuteAsync(
            new ChangeCalendarEventAssigneeCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                calendarEvent.Id,
                other.OtherMembership.Id));
        ChangeCalendarEventAssigneeResult inactive = await useCase.ExecuteAsync(
            new ChangeCalendarEventAssigneeCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                calendarEvent.Id,
                graph.OtherMembership.Id));

        Assert.All(
            new[] { missing, crossTenant, inactive },
            result => Assert.Equal(
                ChangeCalendarEventAssigneeResult.RelatedAssigneeUnavailable,
                result));
        Assert.Null((await FindEventAsync(calendarEvent.Id)).AssigneeMembershipId);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner, false, true)]
    [InlineData(OrganizationRole.Administrator, false, true)]
    [InlineData(OrganizationRole.Member, true, true)]
    [InlineData(OrganizationRole.Member, false, false)]
    public async Task Delete_AuthorizationMatrix_HardDeletesOnlyAuthorizedEvent(
        OrganizationRole role,
        bool actorCreated,
        bool expectedDelete)
    {
        TenantGraph graph = await SeedTenantAsync(role);
        CalendarEvent calendarEvent = CreateEvent(
            graph,
            actorCreated
                ? graph.ActorMembership.Id
                : graph.OtherMembership.Id);
        await SeedAsync(calendarEvent);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();

        DeleteCalendarEventResult result = await CreateDeleteUseCase(queryContext)
            .ExecuteAsync(new DeleteCalendarEventCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                calendarEvent.Id));

        Assert.Equal(
            expectedDelete
                ? DeleteCalendarEventResult.Deleted
                : DeleteCalendarEventResult.AccessDenied,
            result);
        Assert.Equal(!expectedDelete, await EventExistsAsync(calendarEvent.Id));
    }

    [Fact]
    public async Task Mutations_ForeignEventAreNotFoundAndDoNotLeakOrMutate()
    {
        TenantGraph graph = await SeedTenantAsync(OrganizationRole.Owner);
        TenantGraph other = await SeedTenantAsync(
            OrganizationRole.Owner,
            suffix: "foreign-mutation");
        CalendarEvent foreignEvent = CreateEvent(
            other,
            other.ActorMembership.Id);
        await SeedAsync(foreignEvent);
        await using EnmaDbContext queryContext = fixture.CreateDbContext();

        UpdateCalendarEventResult update = await CreateUpdateUseCase(queryContext)
            .ExecuteAsync(CreateUpdateCommand(graph, foreignEvent.Id));
        ChangeCalendarEventAssigneeResult assignment =
            await CreateAssigneeUseCase(queryContext).ExecuteAsync(
                new ChangeCalendarEventAssigneeCommand(
                    graph.ActorUser.Id,
                    graph.Organization.Id,
                    foreignEvent.Id,
                    null));
        DeleteCalendarEventResult delete = await CreateDeleteUseCase(queryContext)
            .ExecuteAsync(new DeleteCalendarEventCommand(
                graph.ActorUser.Id,
                graph.Organization.Id,
                foreignEvent.Id));

        Assert.Equal(UpdateCalendarEventResult.NotFound, update);
        Assert.Equal(ChangeCalendarEventAssigneeResult.NotFound, assignment);
        Assert.Equal(DeleteCalendarEventResult.NotFound, delete);
        Assert.Equal("Original event", (await FindEventAsync(foreignEvent.Id)).Title);
    }

    public static TheoryData<OrganizationRole, AssociationSelection, AssignmentSelection>
        CreateMatrix =>
        new()
        {
            { OrganizationRole.Owner, AssociationSelection.General, AssignmentSelection.None },
            { OrganizationRole.Owner, AssociationSelection.Client, AssignmentSelection.Other },
            { OrganizationRole.Owner, AssociationSelection.Process, AssignmentSelection.Self },
            { OrganizationRole.Administrator, AssociationSelection.General, AssignmentSelection.Other },
            { OrganizationRole.Administrator, AssociationSelection.Client, AssignmentSelection.None },
            { OrganizationRole.Administrator, AssociationSelection.Process, AssignmentSelection.Self },
            { OrganizationRole.Member, AssociationSelection.General, AssignmentSelection.None },
            { OrganizationRole.Member, AssociationSelection.Client, AssignmentSelection.Self },
            { OrganizationRole.Member, AssociationSelection.Process, AssignmentSelection.Self }
        };

    private CreateCalendarEventUseCase CreateCreateUseCase(
        EnmaDbContext queryContext)
    {
        return new CreateCalendarEventUseCase(
            CreateAccessAuthorization(queryContext),
            new CalendarEventActionAuthorization(),
            new ActiveClientInOrganizationLookup(queryContext),
            new ProcessOrganizationOwnershipLookup(queryContext),
            new CalendarEventCreationPersistence(CreateOptions()),
            new FixedTimeProvider(EventCreatedAt));
    }

    private static GetCalendarEventUseCase CreateGetUseCase(
        EnmaDbContext queryContext)
    {
        return new GetCalendarEventUseCase(
            CreateAccessAuthorization(queryContext),
            new CalendarEventActionAuthorization(),
            new CalendarEventReadQueries(queryContext));
    }

    private UpdateCalendarEventUseCase CreateUpdateUseCase(
        EnmaDbContext queryContext)
    {
        return new UpdateCalendarEventUseCase(
            CreateAccessAuthorization(queryContext),
            new CalendarEventActionAuthorization(),
            new CalendarEventMutationPersistence(CreateOptions()));
    }

    private ChangeCalendarEventAssigneeUseCase CreateAssigneeUseCase(
        EnmaDbContext queryContext)
    {
        return new ChangeCalendarEventAssigneeUseCase(
            CreateAccessAuthorization(queryContext),
            new CalendarEventActionAuthorization(),
            new CalendarEventMutationPersistence(CreateOptions()));
    }

    private DeleteCalendarEventUseCase CreateDeleteUseCase(
        EnmaDbContext queryContext)
    {
        return new DeleteCalendarEventUseCase(
            CreateAccessAuthorization(queryContext),
            new CalendarEventActionAuthorization(),
            new CalendarEventMutationPersistence(CreateOptions()));
    }

    private static CalendarEventAccessAuthorization CreateAccessAuthorization(
        EnmaDbContext queryContext)
    {
        return new CalendarEventAccessAuthorization(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(queryContext)));
    }

    private DbContextOptions<EnmaDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
    }

    private static CreateCalendarEventCommand CreateCommand(
        TenantGraph graph,
        Guid? clientId = null,
        Guid? processId = null,
        Guid? assigneeMembershipId = null)
    {
        return new CreateCalendarEventCommand(
            graph.ActorUser.Id,
            graph.Organization.Id,
            "Calendar event",
            null,
            StartsAt,
            EndsAt,
            null,
            clientId,
            processId,
            assigneeMembershipId);
    }

    private static UpdateCalendarEventCommand CreateUpdateCommand(
        TenantGraph graph,
        Guid calendarEventId)
    {
        return new UpdateCalendarEventCommand(
            graph.ActorUser.Id,
            graph.Organization.Id,
            calendarEventId,
            "  Updated hearing  ",
            "  Updated description  ",
            StartsAt,
            EndsAt,
            "  Courtroom 4  ",
            null,
            null);
    }

    private static CalendarEvent CreateEvent(
        TenantGraph graph,
        Guid creatorMembershipId,
        Guid? clientId = null,
        Guid? processId = null,
        Guid? assigneeMembershipId = null)
    {
        return new CalendarEvent(
            graph.Organization.Id,
            "Original event",
            "Original description",
            StartsAt,
            EndsAt,
            "Original location",
            clientId,
            processId,
            assigneeMembershipId,
            creatorMembershipId,
            EventCreatedAt);
    }

    private async Task<TenantGraph> SeedTenantAsync(
        OrganizationRole actorRole,
        string suffix = "primary")
    {
        var organization = new Organization(
            $"Calendar Organization {suffix}",
            $"calendar-organization-{suffix}",
            SeededAt);
        var actorUser = new User(
            $"Actor {suffix}",
            $"calendar-actor-{suffix}@example.test",
            SeededAt);
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actorUser.Id,
            actorRole,
            SeededAt);
        var otherUser = new User(
            $"Other {suffix}",
            $"calendar-other-{suffix}@example.test",
            SeededAt);
        var otherMembership = new OrganizationMembership(
            organization.Id,
            otherUser.Id,
            OrganizationRole.Member,
            SeededAt);
        var client = new Client(
            organization.Id,
            $"Client {suffix}",
            SeededAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            $"Process {suffix}",
            SeededAt);
        await SeedAsync(
            organization,
            actorUser,
            otherUser,
            actorMembership,
            otherMembership,
            client,
            legalProcess);

        return new TenantGraph(
            organization,
            actorUser,
            actorMembership,
            otherUser,
            otherMembership,
            client,
            legalProcess);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private async Task<CalendarEvent> FindEventAsync(Guid calendarEventId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.CalendarEvents
            .AsNoTracking()
            .SingleAsync(calendarEvent => calendarEvent.Id == calendarEventId);
    }

    private async Task<bool> EventExistsAsync(Guid calendarEventId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.CalendarEvents
            .AnyAsync(calendarEvent => calendarEvent.Id == calendarEventId);
    }

    private async Task<int> CountEventsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.CalendarEvents.CountAsync();
    }

    private async Task DeactivateOrganizationAsync(Guid organizationId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = await dbContext.Organizations
            .SingleAsync(candidate => candidate.Id == organizationId);
        organization.Deactivate();
        await dbContext.SaveChangesAsync();
    }

    private async Task DeactivateMembershipAsync(Guid membershipId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembership membership =
            await dbContext.OrganizationMemberships
                .SingleAsync(candidate => candidate.Id == membershipId);
        membership.Deactivate();
        await dbContext.SaveChangesAsync();
    }

    private async Task DeactivateUserAsync(Guid userId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = await dbContext.Users
            .SingleAsync(candidate => candidate.Id == userId);
        user.Deactivate();
        await dbContext.SaveChangesAsync();
    }

    private async Task DeactivateClientAsync(Guid clientId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Client client = await dbContext.Clients
            .SingleAsync(candidate => candidate.Id == clientId);
        client.Deactivate();
        await dbContext.SaveChangesAsync();
    }

    private async Task ChangeRoleAsync(
        Guid membershipId,
        OrganizationRole role)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembership membership =
            await dbContext.OrganizationMemberships
                .SingleAsync(candidate => candidate.Id == membershipId);
        membership.ChangeRole(role);
        await dbContext.SaveChangesAsync();
    }

    private static Guid AssertEventId(CreateCalendarEventResult result)
    {
        return Assert.IsType<Guid>(result.CalendarEventId);
    }

    public enum AssociationSelection
    {
        General = 0,
        Client = 1,
        Process = 2
    }

    public enum AssignmentSelection
    {
        None = 0,
        Self = 1,
        Other = 2
    }

    public enum InactiveActorState
    {
        Organization = 0,
        Membership = 1,
        User = 2
    }

    private sealed record TenantGraph(
        Organization Organization,
        User ActorUser,
        OrganizationMembership ActorMembership,
        User OtherUser,
        OrganizationMembership OtherMembership,
        Client Client,
        LegalProcess Process);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class BeforeCreationPersistence(
        ICalendarEventCreationPersistence inner,
        Func<Task> beforeExecute) : ICalendarEventCreationPersistence
    {
        public async Task<CalendarEventCreationPersistenceResult> ExecuteAsync(
            CalendarEventCreationPersistenceRequest request,
            Func<CalendarEventCreationLockedState, CalendarEventCreationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            await beforeExecute();
            return await inner.ExecuteAsync(request, decide, cancellationToken);
        }
    }
}
