using System.Data.Common;
using System.Reflection;
using Enma.Application.Notifications;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Notifications;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationReadMutationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse(
        "2026-08-01T12:00:00Z");
    private static readonly DateTimeOffset GeneratedAt = DateTimeOffset.Parse(
        "2026-08-24T12:00:00Z");
    private static readonly DateTimeOffset ReadAt = DateTimeOffset.Parse(
        "2026-08-24T13:00:00Z");
    private static readonly DateOnly OccurrenceDate = new(2026, 8, 26);
    private static readonly DateTimeOffset OccurrenceAt = DateTimeOffset.Parse(
        "2026-08-24T14:00:00Z");

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReadFeed_BoundsOrdersCountsOutsideFeedAndUsesTwoQueries()
    {
        TenantGraph graph = CreateGraph("bounded");
        var entities = new List<object>(graph.Entities);
        var expectedTopIds = new List<Guid>();

        for (int index = 0; index < 22; index++)
        {
            var task = new LegalTask(
                graph.Organization.Id,
                $"Bounded task {index}",
                null,
                OccurrenceDate,
                null,
                null,
                graph.Membership.Id,
                CreatedAt.AddMinutes(index));
            DateTimeOffset generatedAt = index < 2
                ? GeneratedAt
                : GeneratedAt.AddMinutes(-index);
            var notification = new Notification(
                graph.Organization.Id,
                graph.User.Id,
                NotificationKind.LegalTaskDueSoon,
                null,
                task.Id,
                null,
                OccurrenceDate,
                null,
                generatedAt);
            Guid notificationId = Guid.Parse(
                $"00000000-0000-0000-0000-{index + 1:000000000000}");
            SetId(notification, notificationId);

            if (index < 20)
            {
                notification.MarkAsRead(ReadAt);
                expectedTopIds.Add(notificationId);
            }

            entities.Add(task);
            entities.Add(notification);
        }

        await SeedAsync(entities);
        expectedTopIds.Sort((left, right) => right.CompareTo(left));
        expectedTopIds = expectedTopIds
            .OrderByDescending(id => id is var value &&
                (value == Guid.Parse("00000000-0000-0000-0000-000000000001") ||
                 value == Guid.Parse("00000000-0000-0000-0000-000000000002"))
                    ? GeneratedAt
                    : GeneratedAt.AddMinutes(-GetSyntheticIndex(value)))
            .ThenByDescending(id => id)
            .ToList();

        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateContext(interceptor);
        var queries = new NotificationReadQueries(dbContext);

        NotificationFeedReadResult result = await queries.ReadFeedAsync(
            graph.Organization.Id,
            graph.User.Id,
            20);

        Assert.Equal(20, result.Items.Count);
        Assert.Equal(expectedTopIds, result.Items.Select(item => item.Id));
        Assert.Equal(2, result.UnreadCount);
        Assert.All(result.Items, item => Assert.NotNull(item.ReadAt));
        Assert.Equal(2, interceptor.CommandTexts.Count);
        string feedSql = Assert.Single(
            interceptor.CommandTexts,
            text => text.Contains("LEFT JOIN legal_deadlines", StringComparison.Ordinal));
        string countSql = Assert.Single(
            interceptor.CommandTexts,
            text => text.Contains("count(*)", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("LEFT JOIN legal_tasks", feedSql);
        Assert.Contains("LEFT JOIN calendar_events", feedSql);
        Assert.Contains("ORDER BY", feedSql);
        Assert.Contains("generated_at", feedSql);
        Assert.Contains("DESC", feedSql);
        Assert.Contains("LIMIT", feedSql);
        Assert.Contains("organization_id", countSql);
        Assert.Contains("recipient_user_id", countSql);
        Assert.Contains("read_at IS NULL", countSql);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ReadFeed_FiltersTenantAndRecipientInsideQuery()
    {
        TenantGraph own = CreateGraph("read-own");
        TenantGraph foreign = CreateGraph("read-foreign");
        User otherUser = CreateUser("read-other");
        var otherMembership = new OrganizationMembership(
            own.Organization.Id,
            otherUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        Notification ownNotification = CreateNotification(
            NotificationKind.LegalDeadlineDueSoon,
            own);
        Notification otherUserNotification = CreateNotification(
            NotificationKind.LegalTaskDueSoon,
            own,
            otherUser.Id);
        Notification foreignNotification = CreateNotification(
            NotificationKind.CalendarEventStartingSoon,
            foreign);

        await SeedAsync(
            own.Entities
                .Concat(foreign.Entities)
                .Concat(
                [
                    otherUser,
                    otherMembership,
                    ownNotification,
                    otherUserNotification,
                    foreignNotification
                ]));
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new NotificationReadQueries(dbContext);

        NotificationFeedReadResult result = await queries.ReadFeedAsync(
            own.Organization.Id,
            own.User.Id,
            20);

        Assert.Equal(ownNotification.Id, Assert.Single(result.Items).Id);
        Assert.Equal(1, result.UnreadCount);
        Assert.DoesNotContain(
            result.Items,
            item => item.Id == otherUserNotification.Id);
        Assert.DoesNotContain(
            result.Items,
            item => item.Id == foreignNotification.Id);
    }

    [Theory]
    [InlineData(NotificationKind.LegalDeadlineDueSoon)]
    [InlineData(NotificationKind.LegalTaskDueSoon)]
    [InlineData(NotificationKind.CalendarEventStartingSoon)]
    public async Task ReadFeed_ProjectsCurrentSourceTitleAndTemporalShape(
        NotificationKind kind)
    {
        TenantGraph graph = CreateGraph($"projection-{(int)kind}");
        Notification notification = CreateNotification(kind, graph);
        await SeedAsync(graph.Entities.Concat([notification]));
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new NotificationReadQueries(dbContext);

        NotificationReadModel item = Assert.Single(
            (await queries.ReadFeedAsync(
                graph.Organization.Id,
                graph.User.Id,
                20)).Items);

        Assert.Equal(kind, item.Kind);
        Assert.Equal(notification.Id, item.Id);
        Assert.Equal(GeneratedAt, item.GeneratedAt);
        Assert.Null(item.ReadAt);

        switch (kind)
        {
            case NotificationKind.LegalDeadlineDueSoon:
                Assert.Equal(graph.Deadline.Id, item.SourceId);
                Assert.Equal(graph.Deadline.Title, item.SourceTitle);
                Assert.Equal(OccurrenceDate, item.OccurrenceDate);
                Assert.Null(item.OccurrenceAt);
                break;
            case NotificationKind.LegalTaskDueSoon:
                Assert.Equal(graph.Task.Id, item.SourceId);
                Assert.Equal(graph.Task.Title, item.SourceTitle);
                Assert.Equal(OccurrenceDate, item.OccurrenceDate);
                Assert.Null(item.OccurrenceAt);
                break;
            case NotificationKind.CalendarEventStartingSoon:
                Assert.Equal(graph.CalendarEvent.Id, item.SourceId);
                Assert.Equal(graph.CalendarEvent.Title, item.SourceTitle);
                Assert.Null(item.OccurrenceDate);
                Assert.Equal(OccurrenceAt, item.OccurrenceAt);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    [Fact]
    public async Task ReadFeed_CanceledRequest_PropagatesCancellation()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new NotificationReadQueries(dbContext);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            queries.ReadFeedAsync(
                Guid.NewGuid(),
                Guid.NewGuid(),
                20,
                cancellationSource.Token));
    }

    [Fact]
    public async Task MarkOne_UpdatesOnlyMatchingTenantAndRecipient()
    {
        TenantGraph own = CreateGraph("mark-one-own");
        TenantGraph foreign = CreateGraph("mark-one-foreign");
        User otherUser = CreateUser("mark-one-other");
        var otherMembership = new OrganizationMembership(
            own.Organization.Id,
            otherUser.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        Notification ownNotification = CreateNotification(
            NotificationKind.LegalDeadlineDueSoon,
            own);
        Notification otherUserNotification = CreateNotification(
            NotificationKind.LegalTaskDueSoon,
            own,
            otherUser.Id);
        Notification foreignNotification = CreateNotification(
            NotificationKind.CalendarEventStartingSoon,
            foreign);
        await SeedAsync(
            own.Entities
                .Concat(foreign.Entities)
                .Concat(
                [
                    otherUser,
                    otherMembership,
                    ownNotification,
                    otherUserNotification,
                    foreignNotification
                ]));
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var persistence = new NotificationMutationPersistence(dbContext);

        Assert.False(await persistence.MarkAsReadAsync(
            otherUserNotification.Id,
            own.Organization.Id,
            own.User.Id,
            ReadAt));
        Assert.False(await persistence.MarkAsReadAsync(
            foreignNotification.Id,
            own.Organization.Id,
            own.User.Id,
            ReadAt));
        Assert.False(await persistence.MarkAsReadAsync(
            Guid.NewGuid(),
            own.Organization.Id,
            own.User.Id,
            ReadAt));
        Assert.True(await persistence.MarkAsReadAsync(
            ownNotification.Id,
            own.Organization.Id,
            own.User.Id,
            ReadAt));

        Dictionary<Guid, DateTimeOffset?> readAtById = await dbContext.Notifications
            .AsNoTracking()
            .ToDictionaryAsync(item => item.Id, item => item.ReadAt);
        Assert.Equal(ReadAt, readAtById[ownNotification.Id]);
        Assert.Null(readAtById[otherUserNotification.Id]);
        Assert.Null(readAtById[foreignNotification.Id]);
    }

    [Fact]
    public async Task MarkOne_AlreadyRead_ReturnsSuccessAndPreservesFirstTimestamp()
    {
        TenantGraph graph = CreateGraph("mark-one-idempotent");
        Notification notification = CreateNotification(
            NotificationKind.LegalTaskDueSoon,
            graph);
        notification.MarkAsRead(ReadAt);
        await SeedAsync(graph.Entities.Concat([notification]));
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var persistence = new NotificationMutationPersistence(dbContext);

        bool found = await persistence.MarkAsReadAsync(
            notification.Id,
            graph.Organization.Id,
            graph.User.Id,
            ReadAt.AddHours(2));

        Assert.True(found);
        Assert.Equal(
            ReadAt,
            await dbContext.Notifications
                .Where(item => item.Id == notification.Id)
                .Select(item => item.ReadAt)
                .SingleAsync());
    }

    [Fact]
    public async Task MarkAll_UpdatesOnlyCurrentRecipientUnreadRows()
    {
        TenantGraph own = CreateGraph("mark-all-own");
        TenantGraph foreign = CreateGraph("mark-all-foreign");
        User otherUser = CreateUser("mark-all-other");
        var otherMembership = new OrganizationMembership(
            own.Organization.Id,
            otherUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        Notification ownDeadline = CreateNotification(
            NotificationKind.LegalDeadlineDueSoon,
            own);
        Notification ownTaskAlreadyRead = CreateNotification(
            NotificationKind.LegalTaskDueSoon,
            own);
        ownTaskAlreadyRead.MarkAsRead(ReadAt.AddMinutes(-10));
        Notification ownEvent = CreateNotification(
            NotificationKind.CalendarEventStartingSoon,
            own);
        Notification otherUserNotification = CreateNotification(
            NotificationKind.LegalTaskDueSoon,
            own,
            otherUser.Id);
        Notification foreignNotification = CreateNotification(
            NotificationKind.LegalDeadlineDueSoon,
            foreign);
        await SeedAsync(
            own.Entities
                .Concat(foreign.Entities)
                .Concat(
                [
                    otherUser,
                    otherMembership,
                    ownDeadline,
                    ownTaskAlreadyRead,
                    ownEvent,
                    otherUserNotification,
                    foreignNotification
                ]));
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var persistence = new NotificationMutationPersistence(dbContext);

        await persistence.MarkAllAsReadAsync(
            own.Organization.Id,
            own.User.Id,
            ReadAt);

        Dictionary<Guid, DateTimeOffset?> readAtById = await dbContext.Notifications
            .AsNoTracking()
            .ToDictionaryAsync(item => item.Id, item => item.ReadAt);
        Assert.Equal(ReadAt, readAtById[ownDeadline.Id]);
        Assert.Equal(ReadAt, readAtById[ownEvent.Id]);
        Assert.Equal(
            ReadAt.AddMinutes(-10),
            readAtById[ownTaskAlreadyRead.Id]);
        Assert.Null(readAtById[otherUserNotification.Id]);
        Assert.Null(readAtById[foreignNotification.Id]);
    }

    [Fact]
    public async Task MarkOne_ConcurrentRequests_KeepOneFirstReadTimestamp()
    {
        TenantGraph graph = CreateGraph("mark-one-concurrent");
        Notification notification = CreateNotification(
            NotificationKind.CalendarEventStartingSoon,
            graph);
        await SeedAsync(graph.Entities.Concat([notification]));
        await using EnmaDbContext firstContext = fixture.CreateDbContext();
        await using EnmaDbContext secondContext = fixture.CreateDbContext();
        var firstPersistence = new NotificationMutationPersistence(firstContext);
        var secondPersistence = new NotificationMutationPersistence(secondContext);
        DateTimeOffset firstCandidate = ReadAt;
        DateTimeOffset secondCandidate = ReadAt.AddMinutes(1);

        bool[] results = await Task.WhenAll(
            firstPersistence.MarkAsReadAsync(
                notification.Id,
                graph.Organization.Id,
                graph.User.Id,
                firstCandidate),
            secondPersistence.MarkAsReadAsync(
                notification.Id,
                graph.Organization.Id,
                graph.User.Id,
                secondCandidate));

        Assert.All(results, Assert.True);
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        DateTimeOffset? establishedReadAt = await verificationContext.Notifications
            .Where(item => item.Id == notification.Id)
            .Select(item => item.ReadAt)
            .SingleAsync();
        Assert.Contains(
            establishedReadAt,
            new DateTimeOffset?[] { firstCandidate, secondCandidate });

        var verificationPersistence = new NotificationMutationPersistence(
            verificationContext);
        Assert.True(await verificationPersistence.MarkAsReadAsync(
            notification.Id,
            graph.Organization.Id,
            graph.User.Id,
            ReadAt.AddHours(1)));
        Assert.Equal(
            establishedReadAt,
            await verificationContext.Notifications
                .Where(item => item.Id == notification.Id)
                .Select(item => item.ReadAt)
                .SingleAsync());
    }

    private EnmaDbContext CreateContext(DbCommandInterceptor interceptor)
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;
        return new EnmaDbContext(options);
    }

    private async Task SeedAsync(IEnumerable<object> entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static TenantGraph CreateGraph(string marker)
    {
        var organization = new Organization(
            $"Notification {marker}",
            $"notification-{marker}-{Guid.NewGuid():N}",
            CreatedAt);
        User user = CreateUser(marker);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt);
        var client = new Client(
            organization.Id,
            $"Client {marker}",
            CreatedAt);
        var process = new LegalProcess(
            organization.Id,
            client.Id,
            $"Process {marker}",
            CreatedAt);
        var deadline = new LegalDeadline(
            organization.Id,
            process.Id,
            $"Deadline {marker}",
            OccurrenceDate,
            CreatedAt);
        var task = new LegalTask(
            organization.Id,
            $"Task {marker}",
            null,
            OccurrenceDate,
            process.Id,
            membership.Id,
            membership.Id,
            CreatedAt);
        var calendarEvent = new CalendarEvent(
            organization.Id,
            $"Event {marker}",
            null,
            OccurrenceAt,
            OccurrenceAt.AddHours(1),
            null,
            null,
            process.Id,
            membership.Id,
            membership.Id,
            CreatedAt);

        return new TenantGraph(
            organization,
            user,
            membership,
            client,
            process,
            deadline,
            task,
            calendarEvent,
            [
                organization,
                user,
                membership,
                client,
                process,
                deadline,
                task,
                calendarEvent
            ]);
    }

    private static User CreateUser(string marker)
    {
        return new User(
            $"Notification {marker}",
            $"notification-{marker}-{Guid.NewGuid():N}@example.test",
            CreatedAt);
    }

    private static Notification CreateNotification(
        NotificationKind kind,
        TenantGraph graph,
        Guid? recipientUserId = null)
    {
        Guid recipient = recipientUserId ?? graph.User.Id;

        return kind switch
        {
            NotificationKind.LegalDeadlineDueSoon => new Notification(
                graph.Organization.Id,
                recipient,
                kind,
                graph.Deadline.Id,
                null,
                null,
                OccurrenceDate,
                null,
                GeneratedAt),
            NotificationKind.LegalTaskDueSoon => new Notification(
                graph.Organization.Id,
                recipient,
                kind,
                null,
                graph.Task.Id,
                null,
                OccurrenceDate,
                null,
                GeneratedAt),
            NotificationKind.CalendarEventStartingSoon => new Notification(
                graph.Organization.Id,
                recipient,
                kind,
                null,
                null,
                graph.CalendarEvent.Id,
                null,
                OccurrenceAt,
                GeneratedAt),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static void SetId(Notification notification, Guid id)
    {
        typeof(Notification)
            .GetProperty(nameof(Notification.Id), BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(notification, id);
    }

    private static int GetSyntheticIndex(Guid id)
    {
        string value = id.ToString("D");
        return int.Parse(value[^12..]) - 1;
    }

    private sealed class ReaderCommandInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commandTexts = [];

        public IReadOnlyList<string> CommandTexts => _commandTexts;

        public override ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commandTexts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed record TenantGraph(
        Organization Organization,
        User User,
        OrganizationMembership Membership,
        Client Client,
        LegalProcess Process,
        LegalDeadline Deadline,
        LegalTask Task,
        CalendarEvent CalendarEvent,
        IReadOnlyList<object> Entities);
}
