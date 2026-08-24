using Enma.Application.Agenda;
using Enma.Application.Authorization;
using Enma.Application.Dashboard;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Dashboard;

public sealed class GetDashboardUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "54d3c289-9b77-4e98-9af7-83ca93413bb8");
    private static readonly Guid OrganizationId = Guid.Parse(
        "31fb18bd-6d3e-42e7-89bf-c2a01053487d");
    private static readonly Guid MembershipId = Guid.Parse(
        "3b46202c-4b42-4cde-b31f-f7e1c006c44c");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-08-24T23:30:00-03:00");

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_LiveSupportedRole_ReadsOrganizationDashboard(
        OrganizationRole role)
    {
        var metricsQueries = new RecordingDashboardReadQueries(CreateMetrics());
        var agendaQueries = new RecordingAgendaReadQueries(CreateUpcoming());
        var timeProvider = new RecordingTimeProvider(Now);
        GetDashboardUseCase useCase = CreateUseCase(
            CreateAccess(role),
            metricsQueries,
            agendaQueries,
            timeProvider);

        GetDashboardResult result = await useCase.ExecuteAsync(CreateQuery());

        Assert.Equal(GetDashboardResultStatus.Succeeded, result.Status);
        Assert.NotNull(result.Dashboard);
        Assert.Equal(1, metricsQueries.CallCount);
        Assert.Equal(1, agendaQueries.UpcomingCallCount);
        Assert.Equal(1, timeProvider.GetUtcNowCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_DeniedAccess_PerformsNoDataReadOrClockCapture()
    {
        var metricsQueries = new RecordingDashboardReadQueries(CreateMetrics());
        var agendaQueries = new RecordingAgendaReadQueries(CreateUpcoming());
        var timeProvider = new RecordingTimeProvider(Now);
        GetDashboardUseCase useCase = CreateUseCase(
            null,
            metricsQueries,
            agendaQueries,
            timeProvider);

        GetDashboardResult result = await useCase.ExecuteAsync(CreateQuery());

        Assert.Same(GetDashboardResult.AccessDenied, result);
        Assert.Equal(0, metricsQueries.CallCount);
        Assert.Equal(0, agendaQueries.UpcomingCallCount);
        Assert.Equal(0, timeProvider.GetUtcNowCallCount);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task ExecuteAsync_MismatchedAuthoritativeAccess_FailsClosedBeforeReads(
        bool mismatchUser,
        bool mismatchOrganization,
        bool missingMembership)
    {
        var access = new OrganizationAccessLookupResult(
            mismatchUser ? Guid.NewGuid() : UserId,
            mismatchOrganization ? Guid.NewGuid() : OrganizationId,
            missingMembership ? null : MembershipId,
            OrganizationRole.Owner);
        var metricsQueries = new RecordingDashboardReadQueries(CreateMetrics());
        var agendaQueries = new RecordingAgendaReadQueries(CreateUpcoming());
        var timeProvider = new RecordingTimeProvider(Now);
        GetDashboardUseCase useCase = CreateUseCase(
            access,
            metricsQueries,
            agendaQueries,
            timeProvider);

        GetDashboardResult result = await useCase.ExecuteAsync(CreateQuery());

        Assert.Same(GetDashboardResult.AccessDenied, result);
        Assert.Equal(0, metricsQueries.CallCount);
        Assert.Equal(0, agendaQueries.UpcomingCallCount);
        Assert.Equal(0, timeProvider.GetUtcNowCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidLiveRole_FailsClosedBeforeReads()
    {
        var access = new OrganizationAccessLookupResult(
            UserId,
            OrganizationId,
            MembershipId,
            (OrganizationRole)999);
        var metricsQueries = new RecordingDashboardReadQueries(CreateMetrics());
        var agendaQueries = new RecordingAgendaReadQueries(CreateUpcoming());
        var timeProvider = new RecordingTimeProvider(Now);
        GetDashboardUseCase useCase = CreateUseCase(
            access,
            metricsQueries,
            agendaQueries,
            timeProvider);

        GetDashboardResult result = await useCase.ExecuteAsync(CreateQuery());

        Assert.Same(GetDashboardResult.AccessDenied, result);
        Assert.Equal(0, metricsQueries.CallCount);
        Assert.Equal(0, agendaQueries.UpcomingCallCount);
        Assert.Equal(0, timeProvider.GetUtcNowCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_CapturesUtcNowOnceAndBuildsExactWindows()
    {
        var metricsQueries = new RecordingDashboardReadQueries(CreateMetrics());
        var agendaQueries = new RecordingAgendaReadQueries(CreateUpcoming());
        var timeProvider = new RecordingTimeProvider(Now);
        GetDashboardUseCase useCase = CreateUseCase(
            CreateAccess(OrganizationRole.Owner),
            metricsQueries,
            agendaQueries,
            timeProvider);

        GetDashboardResult result = await useCase.ExecuteAsync(CreateQuery());

        DashboardReadModel dashboard = Assert.IsType<DashboardReadModel>(
            result.Dashboard);
        var referenceDate = new DateOnly(2026, 8, 25);
        var throughDate = new DateOnly(2026, 9, 1);
        Assert.Equal(1, timeProvider.GetUtcNowCallCount);
        Assert.Equal(referenceDate, dashboard.ReferenceDate);
        Assert.Equal(throughDate, dashboard.ThroughDate);
        Assert.Equal(
            new DashboardMetricsReadRequest(
                OrganizationId,
                referenceDate,
                throughDate),
            metricsQueries.Request);
        Assert.Equal(
            new UpcomingAgendaReadRequest(
                OrganizationId,
                referenceDate,
                throughDate,
                DateTimeOffset.Parse("2026-08-25T02:30:00Z"),
                DateTimeOffset.Parse("2026-09-02T00:00:00Z")),
            agendaQueries.UpcomingRequest);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsCancellationToAccessMetricsAndUpcoming()
    {
        using var cancellationSource = new CancellationTokenSource();
        var accessLookup = new RecordingAccessLookup(
            CreateAccess(OrganizationRole.Member));
        var metricsQueries = new RecordingDashboardReadQueries(CreateMetrics());
        var agendaQueries = new RecordingAgendaReadQueries(CreateUpcoming());
        var useCase = new GetDashboardUseCase(
            new OrganizationAccessAuthorization(accessLookup),
            metricsQueries,
            agendaQueries,
            new RecordingTimeProvider(Now));

        await useCase.ExecuteAsync(
            CreateQuery(),
            cancellationSource.Token);

        Assert.Equal(cancellationSource.Token, accessLookup.CancellationToken);
        Assert.Equal(cancellationSource.Token, metricsQueries.CancellationToken);
        Assert.Equal(cancellationSource.Token, agendaQueries.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_MetricsCancellation_DoesNotStartUpcomingRead()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var metricsQueries = new RecordingDashboardReadQueries(
            _ => Task.FromCanceled<DashboardMetricsReadModel>(
                cancellationSource.Token));
        var agendaQueries = new RecordingAgendaReadQueries(CreateUpcoming());
        GetDashboardUseCase useCase = CreateUseCase(
            CreateAccess(OrganizationRole.Owner),
            metricsQueries,
            agendaQueries,
            new RecordingTimeProvider(Now));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            useCase.ExecuteAsync(CreateQuery(), cancellationSource.Token));

        Assert.Equal(1, metricsQueries.CallCount);
        Assert.Equal(0, agendaQueries.UpcomingCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ComposesMetricsAndGroupedUpcomingWithoutMutation()
    {
        DashboardMetricsReadModel metrics = CreateMetrics();
        UpcomingAgendaReadModel upcoming = CreateUpcoming();
        GetDashboardUseCase useCase = CreateUseCase(
            CreateAccess(OrganizationRole.Administrator),
            new RecordingDashboardReadQueries(metrics),
            new RecordingAgendaReadQueries(upcoming),
            new RecordingTimeProvider(DateTimeOffset.Parse(
                "2026-08-24T12:00:00Z")));

        GetDashboardResult result = await useCase.ExecuteAsync(CreateQuery());

        DashboardReadModel dashboard = Assert.IsType<DashboardReadModel>(
            result.Dashboard);
        Assert.Equal(new DashboardSummaryReadModel(1, 2, 3, 4), dashboard.Summary);
        Assert.Equal(
            new DashboardAttentionReadModel(
                new DashboardAttentionBucketReadModel(5, 6, 7),
                new DashboardAttentionBucketReadModel(8, 9, 10)),
            dashboard.Attention);
        Assert.Same(upcoming, dashboard.Upcoming);
    }

    private static GetDashboardQuery CreateQuery()
    {
        return new GetDashboardQuery(UserId, OrganizationId);
    }

    private static DashboardMetricsReadModel CreateMetrics()
    {
        return new DashboardMetricsReadModel(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
    }

    private static UpcomingAgendaReadModel CreateUpcoming()
    {
        return new UpcomingAgendaReadModel(
            [
                new UpcomingAgendaDeadlineReadModel(
                    Guid.NewGuid(),
                    "Deadline",
                    new DateOnly(2026, 8, 25),
                    "Client",
                    "Process")
            ],
            [
                new UpcomingAgendaTaskReadModel(
                    Guid.NewGuid(),
                    "Task",
                    new DateOnly(2026, 8, 26),
                    null,
                    null,
                    null)
            ],
            [
                new UpcomingAgendaCalendarEventReadModel(
                    Guid.NewGuid(),
                    "Event",
                    DateTimeOffset.Parse("2026-08-27T12:00:00Z"),
                    DateTimeOffset.Parse("2026-08-27T13:00:00Z"),
                    null,
                    null,
                    null)
            ]);
    }

    private static OrganizationAccessLookupResult CreateAccess(
        OrganizationRole role)
    {
        return new OrganizationAccessLookupResult(
            UserId,
            OrganizationId,
            MembershipId,
            role);
    }

    private static GetDashboardUseCase CreateUseCase(
        OrganizationAccessLookupResult? access,
        IDashboardReadQueries metricsQueries,
        IAgendaReadQueries agendaQueries,
        TimeProvider timeProvider)
    {
        return new GetDashboardUseCase(
            new OrganizationAccessAuthorization(
                new RecordingAccessLookup(access)),
            metricsQueries,
            agendaQueries,
            timeProvider);
    }

    private sealed class RecordingAccessLookup(
        OrganizationAccessLookupResult? access) : IOrganizationAccessLookup
    {
        public CancellationToken CancellationToken { get; private set; }

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
            CancellationToken = cancellationToken;
            return Task.FromResult(access);
        }
    }

    private sealed class RecordingDashboardReadQueries : IDashboardReadQueries
    {
        private readonly Func<CancellationToken, Task<DashboardMetricsReadModel>>
            _read;

        public RecordingDashboardReadQueries(DashboardMetricsReadModel metrics)
            : this(_ => Task.FromResult(metrics))
        {
        }

        public RecordingDashboardReadQueries(
            Func<CancellationToken, Task<DashboardMetricsReadModel>> read)
        {
            _read = read;
        }

        public int CallCount { get; private set; }

        public DashboardMetricsReadRequest? Request { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<DashboardMetricsReadModel> ReadMetricsAsync(
            DashboardMetricsReadRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            CancellationToken = cancellationToken;
            return _read(cancellationToken);
        }
    }

    private sealed class RecordingAgendaReadQueries(
        UpcomingAgendaReadModel upcoming) : IAgendaReadQueries
    {
        public int UpcomingCallCount { get; private set; }

        public UpcomingAgendaReadRequest? UpcomingRequest { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<IReadOnlyList<AgendaItemReadModel>> ReadAsync(
            AgendaReadRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException();
        }

        public Task<UpcomingAgendaReadModel> ReadUpcomingAsync(
            UpcomingAgendaReadRequest request,
            CancellationToken cancellationToken = default)
        {
            UpcomingCallCount++;
            UpcomingRequest = request;
            CancellationToken = cancellationToken;
            return Task.FromResult(upcoming);
        }
    }

    private sealed class RecordingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public int GetUtcNowCallCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            GetUtcNowCallCount++;
            return now;
        }
    }
}
