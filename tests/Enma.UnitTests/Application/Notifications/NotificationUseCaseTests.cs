using Enma.Application.Authorization;
using Enma.Application.Notifications;
using Enma.Application.Notifications.List;
using Enma.Application.Notifications.MarkAllRead;
using Enma.Application.Notifications.MarkRead;
using Enma.Domain.Notifications;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Notifications;

public sealed class NotificationUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "cc77c063-1d16-48c9-9722-22c782eabb48");
    private static readonly Guid OrganizationId = Guid.Parse(
        "1c94f143-7e7e-45c6-b6e7-0176fa8f5792");
    private static readonly Guid MembershipId = Guid.Parse(
        "f3617066-3029-4ef8-a983-2859dba4bb93");
    private static readonly Guid NotificationId = Guid.Parse(
        "080cdbdb-eb5c-42da-ad13-14fb2e660ed9");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-08-24T18:30:00-03:00");

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task List_AllLiveRoles_ReadsOnlyRequestedUserAndTenant(
        OrganizationRole role)
    {
        NotificationReadModel item = CreateReadModel();
        var readQueries = new FakeReadQueries(
            new NotificationFeedReadResult([item], 27));
        var useCase = new ListNotificationsUseCase(
            CreateAccessAuthorization(role),
            readQueries);

        ListNotificationsResult result = await useCase.ExecuteAsync(
            new ListNotificationsQuery(UserId, OrganizationId));

        Assert.Equal(ListNotificationsResultStatus.Succeeded, result.Status);
        Assert.Equal(item, Assert.Single(result.Items));
        Assert.Equal(27, result.UnreadCount);
        Assert.Equal(OrganizationId, readQueries.OrganizationId);
        Assert.Equal(UserId, readQueries.RecipientUserId);
        Assert.Equal(ListNotificationsUseCase.MaximumItems, readQueries.MaximumItems);
    }

    [Fact]
    public async Task List_DeniedAccess_PerformsNoRead()
    {
        var readQueries = new FakeReadQueries(
            new NotificationFeedReadResult([], 0));
        var useCase = new ListNotificationsUseCase(
            CreateAccessAuthorization(null),
            readQueries);

        ListNotificationsResult result = await useCase.ExecuteAsync(
            new ListNotificationsQuery(UserId, OrganizationId));

        Assert.Same(ListNotificationsResult.AccessDenied, result);
        Assert.Equal(0, readQueries.CallCount);
    }

    [Fact]
    public async Task List_MismatchedAuthoritativeIdentity_FailsClosed()
    {
        var access = new OrganizationAccessLookupResult(
            Guid.NewGuid(),
            OrganizationId,
            MembershipId,
            OrganizationRole.Owner);
        var readQueries = new FakeReadQueries(
            new NotificationFeedReadResult([], 0));
        var useCase = new ListNotificationsUseCase(
            new OrganizationAccessAuthorization(new StubAccessLookup(access)),
            readQueries);

        ListNotificationsResult result = await useCase.ExecuteAsync(
            new ListNotificationsQuery(UserId, OrganizationId));

        Assert.Same(ListNotificationsResult.AccessDenied, result);
        Assert.Equal(0, readQueries.CallCount);
    }

    [Fact]
    public async Task List_PropagatesCancellationToReadQuery()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var readQueries = new FakeReadQueries(
            new NotificationFeedReadResult([], 0),
            throwOnCancellation: true);
        var useCase = new ListNotificationsUseCase(
            CreateAccessAuthorization(OrganizationRole.Member),
            readQueries);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            useCase.ExecuteAsync(
                new ListNotificationsQuery(UserId, OrganizationId),
                cancellationSource.Token));
    }

    [Fact]
    public async Task MarkOne_Valid_UsesStableUtcTimeAndQualifiedIdentity()
    {
        var persistence = new FakeMutationPersistence(markOneFound: true);
        var useCase = CreateMarkOneUseCase(persistence);
        using var cancellationSource = new CancellationTokenSource();

        MarkNotificationAsReadResult result = await useCase.ExecuteAsync(
            new MarkNotificationAsReadCommand(
                UserId,
                OrganizationId,
                NotificationId),
            cancellationSource.Token);

        Assert.Equal(MarkNotificationAsReadResult.Succeeded, result);
        Assert.Equal(NotificationId, persistence.NotificationId);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
        Assert.Equal(UserId, persistence.RecipientUserId);
        Assert.Equal(Now.ToUniversalTime(), persistence.ReadAt);
        Assert.Equal(cancellationSource.Token, persistence.CancellationToken);
    }

    [Fact]
    public async Task MarkOne_PersistenceDoesNotFindQualifiedRow_ReturnsNotFound()
    {
        var persistence = new FakeMutationPersistence(markOneFound: false);
        var useCase = CreateMarkOneUseCase(persistence);

        MarkNotificationAsReadResult result = await useCase.ExecuteAsync(
            new MarkNotificationAsReadCommand(
                UserId,
                OrganizationId,
                NotificationId));

        Assert.Equal(MarkNotificationAsReadResult.NotFound, result);
    }

    [Fact]
    public async Task MarkOne_EmptyId_ReturnsNotFoundWithoutMutation()
    {
        var persistence = new FakeMutationPersistence(markOneFound: true);
        var useCase = CreateMarkOneUseCase(persistence);

        MarkNotificationAsReadResult result = await useCase.ExecuteAsync(
            new MarkNotificationAsReadCommand(
                UserId,
                OrganizationId,
                Guid.Empty));

        Assert.Equal(MarkNotificationAsReadResult.NotFound, result);
        Assert.Equal(0, persistence.MarkOneCallCount);
    }

    [Fact]
    public async Task MarkOne_DeniedAccess_PerformsNoMutation()
    {
        var persistence = new FakeMutationPersistence(markOneFound: true);
        var useCase = new MarkNotificationAsReadUseCase(
            CreateAccessAuthorization(null),
            persistence,
            new FixedTimeProvider(Now));

        MarkNotificationAsReadResult result = await useCase.ExecuteAsync(
            new MarkNotificationAsReadCommand(
                UserId,
                OrganizationId,
                NotificationId));

        Assert.Equal(MarkNotificationAsReadResult.AccessDenied, result);
        Assert.Equal(0, persistence.MarkOneCallCount);
    }

    [Fact]
    public async Task MarkAll_UsesOneStableUtcTimeAndQualifiedIdentity()
    {
        var persistence = new FakeMutationPersistence(markOneFound: true);
        var useCase = new MarkAllNotificationsAsReadUseCase(
            CreateAccessAuthorization(OrganizationRole.Owner),
            persistence,
            new FixedTimeProvider(Now));
        using var cancellationSource = new CancellationTokenSource();

        MarkAllNotificationsAsReadResult result = await useCase.ExecuteAsync(
            new MarkAllNotificationsAsReadCommand(UserId, OrganizationId),
            cancellationSource.Token);

        Assert.Equal(MarkAllNotificationsAsReadResult.Succeeded, result);
        Assert.Equal(1, persistence.MarkAllCallCount);
        Assert.Equal(OrganizationId, persistence.OrganizationId);
        Assert.Equal(UserId, persistence.RecipientUserId);
        Assert.Equal(Now.ToUniversalTime(), persistence.ReadAt);
        Assert.Equal(cancellationSource.Token, persistence.CancellationToken);
    }

    [Fact]
    public async Task MarkAll_DeniedAccess_PerformsNoMutation()
    {
        var persistence = new FakeMutationPersistence(markOneFound: true);
        var useCase = new MarkAllNotificationsAsReadUseCase(
            CreateAccessAuthorization(null),
            persistence,
            new FixedTimeProvider(Now));

        MarkAllNotificationsAsReadResult result = await useCase.ExecuteAsync(
            new MarkAllNotificationsAsReadCommand(UserId, OrganizationId));

        Assert.Equal(MarkAllNotificationsAsReadResult.AccessDenied, result);
        Assert.Equal(0, persistence.MarkAllCallCount);
    }

    private static MarkNotificationAsReadUseCase CreateMarkOneUseCase(
        INotificationMutationPersistence persistence)
    {
        return new MarkNotificationAsReadUseCase(
            CreateAccessAuthorization(OrganizationRole.Member),
            persistence,
            new FixedTimeProvider(Now));
    }

    private static OrganizationAccessAuthorization CreateAccessAuthorization(
        OrganizationRole? role)
    {
        OrganizationAccessLookupResult? access = role.HasValue
            ? new OrganizationAccessLookupResult(
                UserId,
                OrganizationId,
                MembershipId,
                role.Value)
            : null;

        return new OrganizationAccessAuthorization(new StubAccessLookup(access));
    }

    private static NotificationReadModel CreateReadModel()
    {
        return new NotificationReadModel(
            NotificationId,
            NotificationKind.LegalTaskDueSoon,
            Guid.NewGuid(),
            "Synthetic task",
            new DateOnly(2026, 8, 26),
            null,
            Now.AddMinutes(-5),
            null);
    }

    private sealed class StubAccessLookup(OrganizationAccessLookupResult? access)
        : IOrganizationAccessLookup
    {
        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(access?.Role);
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(access);
        }
    }

    private sealed class FakeReadQueries(
        NotificationFeedReadResult result,
        bool throwOnCancellation = false) : INotificationReadQueries
    {
        public int CallCount { get; private set; }

        public Guid? OrganizationId { get; private set; }

        public Guid? RecipientUserId { get; private set; }

        public int? MaximumItems { get; private set; }

        public Task<NotificationFeedReadResult> ReadFeedAsync(
            Guid organizationId,
            Guid recipientUserId,
            int maximumItems,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            OrganizationId = organizationId;
            RecipientUserId = recipientUserId;
            MaximumItems = maximumItems;

            if (throwOnCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(result);
        }
    }

    private sealed class FakeMutationPersistence(bool markOneFound)
        : INotificationMutationPersistence
    {
        public int MarkOneCallCount { get; private set; }

        public int MarkAllCallCount { get; private set; }

        public Guid? NotificationId { get; private set; }

        public Guid? OrganizationId { get; private set; }

        public Guid? RecipientUserId { get; private set; }

        public DateTimeOffset? ReadAt { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<bool> MarkAsReadAsync(
            Guid notificationId,
            Guid organizationId,
            Guid recipientUserId,
            DateTimeOffset readAt,
            CancellationToken cancellationToken = default)
        {
            MarkOneCallCount++;
            NotificationId = notificationId;
            OrganizationId = organizationId;
            RecipientUserId = recipientUserId;
            ReadAt = readAt;
            CancellationToken = cancellationToken;
            return Task.FromResult(markOneFound);
        }

        public Task MarkAllAsReadAsync(
            Guid organizationId,
            Guid recipientUserId,
            DateTimeOffset readAt,
            CancellationToken cancellationToken = default)
        {
            MarkAllCallCount++;
            OrganizationId = organizationId;
            RecipientUserId = recipientUserId;
            ReadAt = readAt;
            CancellationToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
