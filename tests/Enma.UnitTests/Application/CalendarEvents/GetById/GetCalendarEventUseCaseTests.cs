using Enma.Application.Authorization;
using Enma.Application.CalendarEvents;
using Enma.Application.CalendarEvents.GetById;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.CalendarEvents.GetById;

public sealed class GetCalendarEventUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "296efbde-b516-4a3c-a261-679604ffadbf");
    private static readonly Guid OrganizationId = Guid.Parse(
        "48c64454-7d1a-42f2-9729-8daf2aa47a9d");
    private static readonly Guid MembershipId = Guid.Parse(
        "2bde85e1-271b-4c56-a107-26357d42a507");
    private static readonly Guid CalendarEventId = Guid.Parse(
        "410eb75f-924b-4db9-a3e9-70f35717523a");

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_AllMvpRoles_CanReadTenantQualifiedDetail(
        OrganizationRole role)
    {
        CalendarEventDetailReadModel expected = CreateReadModel();
        var readQueries = new StubReadQueries(expected);
        GetCalendarEventUseCase useCase = CreateUseCase(role, readQueries);

        GetCalendarEventResult result = await useCase.ExecuteAsync(
            new GetCalendarEventQuery(UserId, OrganizationId, CalendarEventId));

        Assert.Equal(GetCalendarEventResultStatus.Succeeded, result.Status);
        Assert.Same(expected, result.CalendarEvent);
        Assert.Equal(CalendarEventId, readQueries.CalendarEventId);
        Assert.Equal(OrganizationId, readQueries.OrganizationId);
    }

    [Fact]
    public async Task ExecuteAsync_MissingOrForeignEvent_ReturnsSameNotFound()
    {
        var readQueries = new StubReadQueries(null);
        GetCalendarEventUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            readQueries);

        GetCalendarEventResult result = await useCase.ExecuteAsync(
            new GetCalendarEventQuery(UserId, OrganizationId, CalendarEventId));

        Assert.Same(GetCalendarEventResult.NotFound, result);
    }

    [Fact]
    public async Task ExecuteAsync_InactiveActor_DeniesBeforeEventLookup()
    {
        var readQueries = new StubReadQueries(CreateReadModel());
        GetCalendarEventUseCase useCase = CreateUseCase(null, readQueries);

        GetCalendarEventResult result = await useCase.ExecuteAsync(
            new GetCalendarEventQuery(UserId, OrganizationId, CalendarEventId));

        Assert.Same(GetCalendarEventResult.AccessDenied, result);
        Assert.Equal(0, readQueries.CallCount);
    }

    private static GetCalendarEventUseCase CreateUseCase(
        OrganizationRole? role,
        ICalendarEventReadQueries readQueries)
    {
        return new GetCalendarEventUseCase(
            new CalendarEventAccessAuthorization(
                new OrganizationAccessAuthorization(new StubAccessLookup(role))),
            new CalendarEventActionAuthorization(),
            readQueries);
    }

    private static CalendarEventDetailReadModel CreateReadModel()
    {
        return new CalendarEventDetailReadModel(
            CalendarEventId,
            "Hearing",
            "Initial hearing",
            DateTimeOffset.Parse("2026-09-01T12:00:00Z"),
            DateTimeOffset.Parse("2026-09-01T13:00:00Z"),
            "Courtroom 2",
            null,
            null,
            Guid.NewGuid(),
            "Contract dispute",
            MembershipId,
            "Assigned User",
            MembershipId,
            "Creator User",
            DateTimeOffset.Parse("2026-08-22T10:00:00Z"));
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
                    MembershipId,
                    role.Value)
                : null;
            return Task.FromResult(result);
        }
    }

    private sealed class StubReadQueries(CalendarEventDetailReadModel? result)
        : ICalendarEventReadQueries
    {
        public int CallCount { get; private set; }

        public Guid CalendarEventId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public Task<CalendarEventDetailReadModel?> FindAsync(
            Guid calendarEventId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CalendarEventId = calendarEventId;
            OrganizationId = organizationId;
            return Task.FromResult(result);
        }
    }
}
