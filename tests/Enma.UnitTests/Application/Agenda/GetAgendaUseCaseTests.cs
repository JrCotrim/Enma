using Enma.Application.Agenda;
using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Agenda;

public sealed class GetAgendaUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "813f562b-58cc-4570-a6d3-5848bf54b354");
    private static readonly Guid OrganizationId = Guid.Parse(
        "ca6c8faf-41d5-40c5-8b21-e75029e0fd31");
    private static readonly Guid MembershipId = Guid.Parse(
        "47fe69d4-5518-43ca-8a43-417dc88d3c3a");
    private static readonly DateTimeOffset From = DateTimeOffset.Parse(
        "2026-09-01T00:00:00-03:00");
    private static readonly DateTimeOffset To = DateTimeOffset.Parse(
        "2026-09-08T00:00:00-03:00");

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_AllSupportedRoles_ReadBoundedTenantAgenda(
        OrganizationRole role)
    {
        var readQueries = new FakeReadQueries([]);
        GetAgendaUseCase useCase = CreateUseCase(role, readQueries);

        GetAgendaResult result = await useCase.ExecuteAsync(
            new GetAgendaQuery(UserId, OrganizationId, From, To));

        Assert.Equal(GetAgendaResultStatus.Succeeded, result.Status);
        Assert.Empty(result.Items);
        Assert.Equal(1, readQueries.CallCount);
        Assert.Equal(OrganizationId, readQueries.Request?.OrganizationId);
        Assert.Equal(new DateOnly(2026, 9, 1),
            readQueries.Request?.LocalStartDate);
        Assert.Equal(new DateOnly(2026, 9, 8),
            readQueries.Request?.LocalEndDate);
        Assert.Equal(From.ToUniversalTime(), readQueries.Request?.FromUtc);
        Assert.Equal(To.ToUniversalTime(), readQueries.Request?.ToUtc);
    }

    [Fact]
    public async Task ExecuteAsync_DeniedAccess_PerformsNoAgendaRead()
    {
        var readQueries = new FakeReadQueries([]);
        GetAgendaUseCase useCase = CreateUseCase(null, readQueries);

        GetAgendaResult result = await useCase.ExecuteAsync(
            new GetAgendaQuery(UserId, OrganizationId, From, To));

        Assert.Same(GetAgendaResult.AccessDenied, result);
        Assert.Equal(0, readQueries.CallCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_EmptyContextIdentifier_DeniesWithoutAgendaRead(
        bool emptyUserId,
        bool emptyOrganizationId)
    {
        var readQueries = new FakeReadQueries([]);
        GetAgendaUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            readQueries);

        GetAgendaResult result = await useCase.ExecuteAsync(new GetAgendaQuery(
            emptyUserId ? Guid.Empty : UserId,
            emptyOrganizationId ? Guid.Empty : OrganizationId,
            From,
            To));

        Assert.Same(GetAgendaResult.AccessDenied, result);
        Assert.Equal(0, readQueries.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidPersistedRole_FailsClosed()
    {
        var readQueries = new FakeReadQueries([]);
        GetAgendaUseCase useCase = CreateUseCase(
            (OrganizationRole)int.MaxValue,
            readQueries);

        GetAgendaResult result = await useCase.ExecuteAsync(
            new GetAgendaQuery(UserId, OrganizationId, From, To));

        Assert.Same(GetAgendaResult.AccessDenied, result);
        Assert.Equal(0, readQueries.CallCount);
    }

    [Theory]
    [MemberData(nameof(MalformedAllowedAccess))]
    public async Task ExecuteAsync_MismatchedAllowedAccess_FailsClosed(
        OrganizationAccessLookupResult access)
    {
        var readQueries = new FakeReadQueries([]);
        var useCase = new GetAgendaUseCase(
            new OrganizationAccessAuthorization(
                new StubAccessLookup(access)),
            readQueries);

        GetAgendaResult result = await useCase.ExecuteAsync(
            new GetAgendaQuery(UserId, OrganizationId, From, To));

        Assert.Same(GetAgendaResult.AccessDenied, result);
        Assert.Equal(0, readQueries.CallCount);
    }

    [Theory]
    [MemberData(nameof(InvalidViewports))]
    public async Task ExecuteAsync_InvalidViewport_ReturnsInvalidInputWithoutAgendaRead(
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var readQueries = new FakeReadQueries([]);
        GetAgendaUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            readQueries);

        GetAgendaResult result = await useCase.ExecuteAsync(
            new GetAgendaQuery(UserId, OrganizationId, from, to));

        Assert.Same(GetAgendaResult.InvalidInput, result);
        Assert.Equal(0, readQueries.CallCount);
    }

    [Theory]
    [MemberData(nameof(NonPositiveLocalCalendarRanges))]
    public async Task ExecuteAsync_NonPositiveLocalCalendarRange_ReturnsInvalidInputWithoutAgendaRead(
        DateTimeOffset from,
        DateTimeOffset to)
    {
        Assert.True(to > from);

        var localStartDate = new DateOnly(from.Year, from.Month, from.Day);
        var localEndDate = new DateOnly(to.Year, to.Month, to.Day);
        Assert.True(localEndDate.DayNumber - localStartDate.DayNumber <= 0);

        var readQueries = new FakeReadQueries([]);
        GetAgendaUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            readQueries);

        GetAgendaResult result = await useCase.ExecuteAsync(
            new GetAgendaQuery(UserId, OrganizationId, from, to));

        Assert.Same(GetAgendaResult.InvalidInput, result);
        Assert.Equal(0, readQueries.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ExactlyNinetyThreeCalendarDays_IsAccepted()
    {
        var readQueries = new FakeReadQueries([]);
        GetAgendaUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            readQueries);
        var from = new DateTimeOffset(
            2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(-2));
        DateOnly localEndDate = new DateOnly(2026, 9, 1)
            .AddDays(GetAgendaUseCase.MaximumCalendarDays);
        var to = new DateTimeOffset(
            localEndDate.Year,
            localEndDate.Month,
            localEndDate.Day,
            0,
            0,
            0,
            TimeSpan.FromHours(-3));

        GetAgendaResult result = await useCase.ExecuteAsync(
            new GetAgendaQuery(UserId, OrganizationId, from, to));

        Assert.Equal(GetAgendaResultStatus.Succeeded, result.Status);
        Assert.True(to - from > TimeSpan.FromDays(
            GetAgendaUseCase.MaximumCalendarDays));
        Assert.Equal(
            GetAgendaUseCase.MaximumCalendarDays,
            readQueries.Request?.LocalEndDate.DayNumber -
                readQueries.Request?.LocalStartDate.DayNumber);
    }

    [Fact]
    public async Task ExecuteAsync_ConsecutiveLocalDatesWithDifferentOffsets_RemainAccepted()
    {
        var readQueries = new FakeReadQueries([]);
        GetAgendaUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            readQueries);
        var from = new DateTimeOffset(
            2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(-3));
        var to = new DateTimeOffset(
            2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(-2));

        GetAgendaResult result = await useCase.ExecuteAsync(
            new GetAgendaQuery(UserId, OrganizationId, from, to));

        Assert.Equal(GetAgendaResultStatus.Succeeded, result.Status);
        Assert.Equal(new DateOnly(2026, 10, 25),
            readQueries.Request?.LocalStartDate);
        Assert.Equal(new DateOnly(2026, 10, 26),
            readQueries.Request?.LocalEndDate);
        Assert.Equal(from.ToUniversalTime(), readQueries.Request?.FromUtc);
        Assert.Equal(to.ToUniversalTime(), readQueries.Request?.ToUtc);
    }

    [Fact]
    public async Task ExecuteAsync_MoreThanNinetyThreeLocalCalendarDays_IsRejected()
    {
        DateTimeOffset to = From.AddDays(
            GetAgendaUseCase.MaximumCalendarDays + 1);
        var readQueries = new FakeReadQueries([]);
        GetAgendaUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            readQueries);

        GetAgendaResult result = await useCase.ExecuteAsync(
            new GetAgendaQuery(UserId, OrganizationId, From, to));

        Assert.Same(GetAgendaResult.InvalidInput, result);
        Assert.Equal(0, readQueries.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_MixedItems_UsesKindThenRealTemporalFieldsOrdering()
    {
        AgendaItemReadModel[] unordered =
        [
            CreateItem(
                AgendaItemKind.CalendarEvent,
                "00000000-0000-0000-0000-000000000004",
                startsAt: DateTimeOffset.Parse("2026-09-01T11:00:00Z"),
                endsAt: DateTimeOffset.Parse("2026-09-01T12:00:00Z")),
            CreateItem(
                AgendaItemKind.Task,
                "00000000-0000-0000-0000-000000000003",
                date: new DateOnly(2026, 9, 2)),
            CreateItem(
                AgendaItemKind.Deadline,
                "00000000-0000-0000-0000-000000000002",
                date: new DateOnly(2026, 9, 2)),
            CreateItem(
                AgendaItemKind.Deadline,
                "00000000-0000-0000-0000-000000000001",
                date: new DateOnly(2026, 9, 1)),
            CreateItem(
                AgendaItemKind.CalendarEvent,
                "00000000-0000-0000-0000-000000000005",
                startsAt: DateTimeOffset.Parse("2026-09-01T10:00:00Z"),
                endsAt: DateTimeOffset.Parse("2026-09-01T11:00:00Z"))
        ];
        var readQueries = new FakeReadQueries(unordered);
        GetAgendaUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            readQueries);

        GetAgendaResult result = await useCase.ExecuteAsync(
            new GetAgendaQuery(UserId, OrganizationId, From, To));

        Assert.Equal(
            [
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Guid.Parse("00000000-0000-0000-0000-000000000002"),
                Guid.Parse("00000000-0000-0000-0000-000000000003"),
                Guid.Parse("00000000-0000-0000-0000-000000000005"),
                Guid.Parse("00000000-0000-0000-0000-000000000004")
            ],
            result.Items.Select(item => item.Id));
    }

    [Fact]
    public void AgendaItemContract_ContainsOnlyAgendaPresentationFields()
    {
        Assert.Equal(
            [
                nameof(AgendaItemReadModel.Kind),
                nameof(AgendaItemReadModel.Id),
                nameof(AgendaItemReadModel.Title),
                nameof(AgendaItemReadModel.IsAllDay),
                nameof(AgendaItemReadModel.Date),
                nameof(AgendaItemReadModel.StartsAt),
                nameof(AgendaItemReadModel.EndsAt),
                nameof(AgendaItemReadModel.CompletedAt),
                nameof(AgendaItemReadModel.ClientId),
                nameof(AgendaItemReadModel.ClientName),
                nameof(AgendaItemReadModel.ProcessId),
                nameof(AgendaItemReadModel.ProcessTitle),
                nameof(AgendaItemReadModel.AssigneeMembershipId),
                nameof(AgendaItemReadModel.AssigneeDisplayName)
            ],
            typeof(AgendaItemReadModel)
                .GetProperties()
                .Select(property => property.Name));
        Assert.Equal(
            [
                AgendaItemKind.Deadline,
                AgendaItemKind.Task,
                AgendaItemKind.CalendarEvent
            ],
            Enum.GetValues<AgendaItemKind>());
    }

    public static TheoryData<DateTimeOffset, DateTimeOffset> InvalidViewports =>
        new()
        {
            { From, From },
            { To, From },
            { DateTimeOffset.MinValue, To },
            { From, DateTimeOffset.MinValue },
            { From.AddMinutes(1), To },
            { From, To.AddSeconds(1) }
        };

    public static TheoryData<DateTimeOffset, DateTimeOffset>
        NonPositiveLocalCalendarRanges =>
        new()
        {
            {
                new DateTimeOffset(
                    2026, 9, 2, 0, 0, 0, TimeSpan.FromHours(14)),
                new DateTimeOffset(
                    2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(-12))
            },
            {
                new DateTimeOffset(
                    2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(14)),
                new DateTimeOffset(
                    2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(-12))
            }
        };

    public static TheoryData<OrganizationAccessLookupResult>
        MalformedAllowedAccess =>
        new()
        {
            new OrganizationAccessLookupResult(
                Guid.NewGuid(),
                OrganizationId,
                MembershipId,
                OrganizationRole.Member),
            new OrganizationAccessLookupResult(
                UserId,
                Guid.NewGuid(),
                MembershipId,
                OrganizationRole.Member),
            new OrganizationAccessLookupResult(
                UserId,
                OrganizationId,
                null,
                OrganizationRole.Member)
        };

    private static GetAgendaUseCase CreateUseCase(
        OrganizationRole? role,
        IAgendaReadQueries readQueries)
    {
        return new GetAgendaUseCase(
            new OrganizationAccessAuthorization(new StubAccessLookup(role)),
            readQueries);
    }

    private static AgendaItemReadModel CreateItem(
        AgendaItemKind kind,
        string id,
        DateOnly? date = null,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? endsAt = null)
    {
        return new AgendaItemReadModel(
            kind,
            Guid.Parse(id),
            kind.ToString(),
            kind != AgendaItemKind.CalendarEvent,
            date,
            startsAt,
            endsAt,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    private sealed class StubAccessLookup : IOrganizationAccessLookup
    {
        private readonly OrganizationAccessLookupResult? _access;

        public StubAccessLookup(OrganizationRole? role)
        {
            _access = role.HasValue
                ? new OrganizationAccessLookupResult(
                    UserId,
                    OrganizationId,
                    MembershipId,
                    role.Value)
                : null;
        }

        public StubAccessLookup(OrganizationAccessLookupResult access)
        {
            _access = access;
        }

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
            return Task.FromResult(_access);
        }
    }

    private sealed class FakeReadQueries(
        IReadOnlyList<AgendaItemReadModel> items) : IAgendaReadQueries
    {
        public int CallCount { get; private set; }

        public AgendaReadRequest? Request { get; private set; }

        public Task<IReadOnlyList<AgendaItemReadModel>> ReadAsync(
            AgendaReadRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            return Task.FromResult(items);
        }
    }
}
