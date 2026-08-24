using Enma.Domain.CalendarEvents;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Notifications;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string RecipientForeignKey =
        "fk_notifications_memberships_org_recipient_user_id";
    private const string DeadlineForeignKey =
        "fk_notifications_deadlines_org_legal_deadline_id";
    private const string TaskForeignKey =
        "fk_notifications_tasks_org_legal_task_id";
    private const string CalendarEventForeignKey =
        "fk_notifications_calendar_events_org_calendar_event_id";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        20,
        12,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateTimeOffset GeneratedAt = CreatedAt.AddDays(5);
    private static readonly DateOnly DueDate = new(2026, 9, 1);
    private static readonly DateTimeOffset StartsAt = new(
        2026,
        9,
        1,
        13,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SaveAndLoad_WithValidRecipientMembership_PreservesAllFields()
    {
        TenantGraph graph = CreateTenantGraph("Tenant A", "notification-a");
        await SeedAsync(GetGraphEntities(graph));
        Notification notification = CreateNotification(
            NotificationKind.LegalDeadlineDueSoon,
            graph);
        notification.MarkAsRead(GeneratedAt.AddMinutes(5));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Notifications.Add(notification);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        Notification persisted = await dbContext.Notifications
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(notification.Id, persisted.Id);
        Assert.Equal(graph.Organization.Id, persisted.OrganizationId);
        Assert.Equal(graph.RecipientUser.Id, persisted.RecipientUserId);
        Assert.Equal(NotificationKind.LegalDeadlineDueSoon, persisted.Kind);
        Assert.Equal(graph.LegalDeadline.Id, persisted.LegalDeadlineId);
        Assert.Null(persisted.LegalTaskId);
        Assert.Null(persisted.CalendarEventId);
        Assert.Equal(DueDate, persisted.OccurrenceDate);
        Assert.Null(persisted.OccurrenceAt);
        Assert.Equal(GeneratedAt, persisted.GeneratedAt);
        Assert.Equal(GeneratedAt.AddMinutes(5), persisted.ReadAt);
    }

    [Fact]
    public async Task SaveChanges_WithRecipientFromAnotherTenant_IsRejected()
    {
        TenantGraph tenantA = CreateTenantGraph("Tenant A", "recipient-a");
        TenantGraph tenantB = CreateTenantGraph("Tenant B", "recipient-b");
        await SeedAsync(GetGraphEntities(tenantA).Concat(GetGraphEntities(tenantB)));
        var notification = new Notification(
            tenantA.Organization.Id,
            tenantB.RecipientUser.Id,
            NotificationKind.LegalDeadlineDueSoon,
            tenantA.LegalDeadline.Id,
            null,
            null,
            DueDate,
            null,
            GeneratedAt);

        DbUpdateException exception = await SaveInvalidAsync(notification);

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            RecipientForeignKey);
    }

    [Theory]
    [InlineData(NotificationKind.LegalDeadlineDueSoon, DeadlineForeignKey)]
    [InlineData(NotificationKind.LegalTaskDueSoon, TaskForeignKey)]
    [InlineData(NotificationKind.CalendarEventStartingSoon, CalendarEventForeignKey)]
    public async Task SaveChanges_WithSourceFromAnotherTenant_IsRejected(
        NotificationKind kind,
        string expectedConstraintName)
    {
        TenantGraph tenantA = CreateTenantGraph("Tenant A", $"source-a-{kind}");
        TenantGraph tenantB = CreateTenantGraph("Tenant B", $"source-b-{kind}");
        await SeedAsync(GetGraphEntities(tenantA).Concat(GetGraphEntities(tenantB)));
        Notification notification = CreateNotification(kind, tenantA, tenantB);

        DbUpdateException exception = await SaveInvalidAsync(notification);

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            expectedConstraintName);
    }

    [Theory]
    [InlineData(
        NotificationKind.LegalDeadlineDueSoon,
        "ux_notifications_deadline_dedupe")]
    [InlineData(
        NotificationKind.LegalTaskDueSoon,
        "ux_notifications_task_dedupe")]
    [InlineData(
        NotificationKind.CalendarEventStartingSoon,
        "ux_notifications_calendar_event_dedupe")]
    public async Task SaveChanges_WithRepeatedDedupeIdentity_IsRejected(
        NotificationKind kind,
        string expectedConstraintName)
    {
        TenantGraph graph = CreateTenantGraph("Dedupe", $"dedupe-{kind}");
        await SeedAsync(GetGraphEntities(graph));

        await using (EnmaDbContext firstContext = fixture.CreateDbContext())
        {
            firstContext.Notifications.Add(CreateNotification(kind, graph));
            await firstContext.SaveChangesAsync();
        }

        DbUpdateException exception = await SaveInvalidAsync(
            CreateNotification(kind, graph, generatedAt: GeneratedAt.AddMinutes(1)));

        AssertPostgresException(
            exception,
            PostgresErrorCodes.UniqueViolation,
            expectedConstraintName);
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.Equal(1, await verificationContext.Notifications.CountAsync());
    }

    [Fact]
    public async Task ConcurrentInsert_WithSameDedupeIdentity_PersistsOnlyOne()
    {
        TenantGraph graph = CreateTenantGraph("Concurrent", "notification-concurrent");
        await SeedAsync(GetGraphEntities(graph));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using EnmaDbContext firstContext = fixture.CreateDbContext();
        await using EnmaDbContext secondContext = fixture.CreateDbContext();
        await using IDbContextTransaction firstTransaction =
            await firstContext.Database.BeginTransactionAsync(timeout.Token);
        firstContext.Notifications.Add(CreateNotification(
            NotificationKind.LegalDeadlineDueSoon,
            graph));
        await firstContext.SaveChangesAsync(timeout.Token);
        secondContext.Notifications.Add(CreateNotification(
            NotificationKind.LegalDeadlineDueSoon,
            graph,
            generatedAt: GeneratedAt.AddMinutes(1)));
        Task<DbUpdateException>? competingInsert = null;

        try
        {
            competingInsert = CaptureDbUpdateExceptionAsync(() =>
                secondContext.SaveChangesAsync(timeout.Token));
            await WaitForBlockedNotificationInsertAsync(timeout.Token);
            Assert.False(competingInsert.IsCompleted);

            await firstTransaction.CommitAsync(timeout.Token);
            DbUpdateException exception = await competingInsert.WaitAsync(
                timeout.Token);
            AssertPostgresException(
                exception,
                PostgresErrorCodes.UniqueViolation,
                "ux_notifications_deadline_dedupe");
        }
        finally
        {
            if (firstTransaction.GetDbTransaction().Connection is not null)
            {
                await firstTransaction.RollbackAsync(CancellationToken.None);
            }

            await DrainTaskAsync(competingInsert);
        }

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.Equal(1, await verificationContext.Notifications.CountAsync());
    }

    [Fact]
    public async Task SaveChanges_WithUnrelatedForeignKeyFailure_RemainsDistinguishable()
    {
        TenantGraph graph = CreateTenantGraph("Failure", "notification-failure");
        await SeedAsync(GetGraphEntities(graph));
        var notification = new Notification(
            graph.Organization.Id,
            graph.RecipientUser.Id,
            NotificationKind.LegalDeadlineDueSoon,
            Guid.NewGuid(),
            null,
            null,
            DueDate,
            null,
            GeneratedAt);

        DbUpdateException exception = await SaveInvalidAsync(notification);

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            DeadlineForeignKey);
        Assert.NotEqual(
            PostgresErrorCodes.UniqueViolation,
            ((PostgresException)exception.InnerException!).SqlState);
    }

    [Fact]
    public async Task DeleteMembership_WhenReferencedByNotification_IsRestricted()
    {
        TenantGraph graph = CreateTenantGraph("Restrict", "notification-restrict");
        await SeedAsync(GetDeadlineGraphEntities(graph));
        await SeedAsync(CreateNotification(
            NotificationKind.LegalDeadlineDueSoon,
            graph));
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.OrganizationMemberships
                .Where(membership => membership.Id == graph.RecipientMembership.Id)
                .ExecuteDeleteAsync());

        Assert.Equal(PostgresErrorCodes.RestrictViolation, exception.SqlState);
        Assert.Equal(RecipientForeignKey, exception.ConstraintName);
        Assert.Equal(1, await dbContext.Notifications.CountAsync());
    }

    [Theory]
    [InlineData(NotificationKind.LegalDeadlineDueSoon)]
    [InlineData(NotificationKind.LegalTaskDueSoon)]
    [InlineData(NotificationKind.CalendarEventStartingSoon)]
    public async Task DeleteSource_RemovesRelatedNotification(
        NotificationKind kind)
    {
        TenantGraph graph = CreateTenantGraph("Cascade", $"notification-cascade-{kind}");
        await SeedAsync(GetGraphEntities(graph));
        Notification notification = CreateNotification(kind, graph);
        await SeedAsync(notification);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        switch (kind)
        {
            case NotificationKind.LegalDeadlineDueSoon:
                await dbContext.LegalDeadlines
                    .Where(deadline => deadline.Id == graph.LegalDeadline.Id)
                    .ExecuteDeleteAsync();
                break;
            case NotificationKind.LegalTaskDueSoon:
                await dbContext.LegalTasks
                    .Where(legalTask => legalTask.Id == graph.LegalTask.Id)
                    .ExecuteDeleteAsync();
                break;
            case NotificationKind.CalendarEventStartingSoon:
                await dbContext.CalendarEvents
                    .Where(calendarEvent => calendarEvent.Id == graph.CalendarEvent.Id)
                    .ExecuteDeleteAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Assert.DoesNotContain(
            await dbContext.Notifications.AsNoTracking().ToArrayAsync(),
            candidate => candidate.Id == notification.Id);
    }

    [Fact]
    public async Task DirectInsert_WithMultipleSources_IsRejected()
    {
        TenantGraph graph = CreateTenantGraph("Check", "notification-check-source");
        await SeedAsync(GetGraphEntities(graph));

        PostgresException exception = await ExecuteInvalidInsertAsync(
            graph,
            NotificationKind.LegalDeadlineDueSoon,
            graph.LegalDeadline.Id,
            graph.LegalTask.Id,
            null,
            DueDate,
            null,
            GeneratedAt,
            null);

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(
            "ck_notifications_exactly_one_source",
            exception.ConstraintName);
    }

    [Theory]
    [InlineData(true, "ck_notifications_kind_source")]
    [InlineData(false, "ck_notifications_occurrence")]
    public async Task DirectInsert_WithInvalidKindSourceOrOccurrenceShape_IsRejected(
        bool mismatchKind,
        string expectedConstraintName)
    {
        TenantGraph graph = CreateTenantGraph(
            "Shape",
            $"notification-shape-{mismatchKind}");
        await SeedAsync(GetGraphEntities(graph));

        PostgresException exception = await ExecuteInvalidInsertAsync(
            graph,
            mismatchKind
                ? NotificationKind.LegalTaskDueSoon
                : NotificationKind.LegalDeadlineDueSoon,
            graph.LegalDeadline.Id,
            null,
            null,
            mismatchKind ? DueDate : null,
            mismatchKind ? null : StartsAt,
            GeneratedAt,
            null);

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(expectedConstraintName, exception.ConstraintName);
    }

    [Fact]
    public async Task DirectInsert_WithReadBeforeGenerated_IsRejected()
    {
        TenantGraph graph = CreateTenantGraph("Read", "notification-read-check");
        await SeedAsync(GetGraphEntities(graph));

        PostgresException exception = await ExecuteInvalidInsertAsync(
            graph,
            NotificationKind.LegalDeadlineDueSoon,
            graph.LegalDeadline.Id,
            null,
            null,
            DueDate,
            null,
            GeneratedAt,
            GeneratedAt.AddTicks(-1));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_notifications_read_at", exception.ConstraintName);
    }

    [Fact]
    public void NotificationModel_HasExpectedTenantQualifiedRelationshipsAndIndexes()
    {
        using EnmaDbContext dbContext = fixture.CreateDbContext();
        IEntityType entityType = Assert.IsAssignableFrom<IEntityType>(
            dbContext.Model.FindEntityType(typeof(Notification)));

        Assert.Equal("notifications", entityType.GetTableName());
        Assert.Equal(
            [
                nameof(Notification.Id),
                nameof(Notification.OrganizationId),
                nameof(Notification.RecipientUserId),
                nameof(Notification.Kind),
                nameof(Notification.LegalDeadlineId),
                nameof(Notification.LegalTaskId),
                nameof(Notification.CalendarEventId),
                nameof(Notification.OccurrenceDate),
                nameof(Notification.OccurrenceAt),
                nameof(Notification.GeneratedAt),
                nameof(Notification.ReadAt)
            ],
            entityType.GetProperties()
                .OrderBy(property => PropertyOrder(property.Name))
                .Select(property => property.Name)
                .ToArray());
        Assert.Equal(
            ValueGenerated.Never,
            entityType.FindProperty(nameof(Notification.Id))!.ValueGenerated);
        Assert.Equal(
            "date",
            entityType.FindProperty(nameof(Notification.OccurrenceDate))!
                .GetColumnType());
        Assert.Equal(
            "timestamp with time zone",
            entityType.FindProperty(nameof(Notification.OccurrenceAt))!
                .GetColumnType());
        Assert.Equal(
            "timestamp with time zone",
            entityType.FindProperty(nameof(Notification.GeneratedAt))!
                .GetColumnType());
        Assert.Empty(entityType.GetNavigations());

        AssertIndex(
            entityType,
            "ux_notifications_deadline_dedupe",
            [
                nameof(Notification.OrganizationId),
                nameof(Notification.LegalDeadlineId),
                nameof(Notification.RecipientUserId),
                nameof(Notification.Kind),
                nameof(Notification.OccurrenceDate)
            ],
            true,
            "legal_deadline_id IS NOT NULL");
        AssertIndex(
            entityType,
            "ux_notifications_task_dedupe",
            [
                nameof(Notification.OrganizationId),
                nameof(Notification.LegalTaskId),
                nameof(Notification.RecipientUserId),
                nameof(Notification.Kind),
                nameof(Notification.OccurrenceDate)
            ],
            true,
            "legal_task_id IS NOT NULL");
        AssertIndex(
            entityType,
            "ux_notifications_calendar_event_dedupe",
            [
                nameof(Notification.OrganizationId),
                nameof(Notification.CalendarEventId),
                nameof(Notification.RecipientUserId),
                nameof(Notification.Kind),
                nameof(Notification.OccurrenceAt)
            ],
            true,
            "calendar_event_id IS NOT NULL");
        AssertIndex(
            entityType,
            "ix_notifications_organization_id_recipient_user_id",
            [
                nameof(Notification.OrganizationId),
                nameof(Notification.RecipientUserId)
            ],
            false,
            null);
        Assert.Equal(4, entityType.GetIndexes().Count());

        AssertForeignKey(
            entityType,
            RecipientForeignKey,
            typeof(OrganizationMembership),
            [
                nameof(Notification.OrganizationId),
                nameof(Notification.RecipientUserId)
            ],
            [
                nameof(OrganizationMembership.OrganizationId),
                nameof(OrganizationMembership.UserId)
            ],
            DeleteBehavior.Restrict);
        AssertForeignKey(
            entityType,
            DeadlineForeignKey,
            typeof(LegalDeadline),
            [
                nameof(Notification.OrganizationId),
                nameof(Notification.LegalDeadlineId)
            ],
            [nameof(LegalDeadline.OrganizationId), nameof(LegalDeadline.Id)],
            DeleteBehavior.Cascade);
        AssertForeignKey(
            entityType,
            TaskForeignKey,
            typeof(LegalTask),
            [
                nameof(Notification.OrganizationId),
                nameof(Notification.LegalTaskId)
            ],
            [nameof(LegalTask.OrganizationId), nameof(LegalTask.Id)],
            DeleteBehavior.Cascade);
        AssertForeignKey(
            entityType,
            CalendarEventForeignKey,
            typeof(CalendarEvent),
            [
                nameof(Notification.OrganizationId),
                nameof(Notification.CalendarEventId)
            ],
            [nameof(CalendarEvent.OrganizationId), nameof(CalendarEvent.Id)],
            DeleteBehavior.Cascade);
        Assert.Equal(4, entityType.GetForeignKeys().Count());
    }

    [Fact]
    public async Task PostgreSqlSchema_HasExpectedConstraintsIndexesAndUniqueStructures()
    {
        Assert.Equal(
            "id,organization_id,recipient_user_id,kind,legal_deadline_id," +
            "legal_task_id,calendar_event_id,occurrence_date,occurrence_at," +
            "generated_at,read_at",
            await GetTableColumnsAsync("notifications"));
        Assert.Equal(
            [
                "ck_notifications_exactly_one_source",
                "ck_notifications_kind",
                "ck_notifications_kind_source",
                "ck_notifications_occurrence",
                "ck_notifications_read_at"
            ],
            await GetCheckConstraintNamesAsync());
        Assert.Equal(
            [
                "ix_notifications_organization_id_recipient_user_id",
                "pk_notifications",
                "ux_notifications_calendar_event_dedupe",
                "ux_notifications_deadline_dedupe",
                "ux_notifications_task_dedupe"
            ],
            await GetIndexNamesAsync("notifications"));

        Assert.Equal(
            "organization_id,recipient_user_id",
            await GetConstraintColumnsAsync("notifications", RecipientForeignKey));
        Assert.Equal(
            "organization_id,legal_deadline_id",
            await GetConstraintColumnsAsync("notifications", DeadlineForeignKey));
        Assert.Equal(
            "organization_id,legal_task_id",
            await GetConstraintColumnsAsync("notifications", TaskForeignKey));
        Assert.Equal(
            "organization_id,calendar_event_id",
            await GetConstraintColumnsAsync(
                "notifications",
                CalendarEventForeignKey));
        Assert.Equal("RESTRICT", await GetDeleteRuleAsync(RecipientForeignKey));
        Assert.Equal("CASCADE", await GetDeleteRuleAsync(DeadlineForeignKey));
        Assert.Equal("CASCADE", await GetDeleteRuleAsync(TaskForeignKey));
        Assert.Equal("CASCADE", await GetDeleteRuleAsync(CalendarEventForeignKey));

        await AssertIndexDefinitionAsync(
            "notifications",
            "ux_notifications_deadline_dedupe",
            "(organization_id, legal_deadline_id, recipient_user_id, kind, " +
            "occurrence_date)",
            "WHERE (legal_deadline_id IS NOT NULL)");
        await AssertIndexDefinitionAsync(
            "notifications",
            "ux_notifications_task_dedupe",
            "(organization_id, legal_task_id, recipient_user_id, kind, " +
            "occurrence_date)",
            "WHERE (legal_task_id IS NOT NULL)");
        await AssertIndexDefinitionAsync(
            "notifications",
            "ux_notifications_calendar_event_dedupe",
            "(organization_id, calendar_event_id, recipient_user_id, kind, " +
            "occurrence_at)",
            "WHERE (calendar_event_id IS NOT NULL)");

        Assert.Equal(
            "organization_id,user_id",
            await GetUniqueConstraintColumnsAsync(
                "organization_memberships",
                "ux_organization_memberships_organization_id_user_id"));
        Assert.Equal(
            1,
            await CountIndexesByColumnsAsync(
                "organization_memberships",
                "(organization_id, user_id)"));
        Assert.Equal(
            "organization_id,id",
            await GetUniqueConstraintColumnsAsync(
                "legal_deadlines",
                "ak_legal_deadlines_organization_id_id"));
        Assert.Equal(
            "organization_id,id",
            await GetUniqueConstraintColumnsAsync(
                "legal_tasks",
                "ak_legal_tasks_organization_id_id"));
        Assert.Equal(
            "organization_id,id",
            await GetUniqueConstraintColumnsAsync(
                "calendar_events",
                "ak_calendar_events_organization_id_id"));

        await AssertIndexDefinitionAsync(
            "legal_deadlines",
            "ix_legal_deadlines_pending_due_date_organization_id_id",
            "(due_date, organization_id, id)",
            "WHERE (completed_at IS NULL)");
        await AssertIndexDefinitionAsync(
            "legal_tasks",
            "ix_legal_tasks_pending_due_date_organization_id_id",
            "(due_date, organization_id, id)",
            "WHERE ((completed_at IS NULL) AND (due_date IS NOT NULL))");
        await AssertIndexDefinitionAsync(
            "calendar_events",
            "ix_calendar_events_starts_at_organization_id_id",
            "(starts_at, organization_id, id)",
            null);
    }

    private async Task<DbUpdateException> SaveInvalidAsync(
        Notification notification)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Notifications.Add(notification);
        return await Assert.ThrowsAsync<DbUpdateException>(() =>
            dbContext.SaveChangesAsync());
    }

    private async Task<PostgresException> ExecuteInvalidInsertAsync(
        TenantGraph graph,
        NotificationKind kind,
        Guid? legalDeadlineId,
        Guid? legalTaskId,
        Guid? calendarEventId,
        DateOnly? occurrenceDate,
        DateTimeOffset? occurrenceAt,
        DateTimeOffset generatedAt,
        DateTimeOffset? readAt)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        return await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO notifications
                    (id, organization_id, recipient_user_id, kind,
                     legal_deadline_id, legal_task_id, calendar_event_id,
                     occurrence_date, occurrence_at, generated_at, read_at)
                VALUES
                    ({Guid.NewGuid()}, {graph.Organization.Id},
                     {graph.RecipientUser.Id}, {(int)kind}, {legalDeadlineId},
                     {legalTaskId}, {calendarEventId}, {occurrenceDate},
                     {occurrenceAt}, {generatedAt}, {readAt})
                """));
    }

    private async Task WaitForBlockedNotificationInsertAsync(
        CancellationToken cancellationToken)
    {
        const string InsertPattern = "%INSERT INTO notifications%";
        await using EnmaDbContext observationContext = fixture.CreateDbContext();

        while (true)
        {
            int waitingCommandCount = await observationContext.Database
                .SqlQuery<int>(
                    $"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND wait_event_type = 'Lock'
                      AND query ILIKE {InsertPattern}
                    """)
                .SingleAsync(cancellationToken);

            if (waitingCommandCount > 0)
            {
                return;
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task SeedAsync(IEnumerable<object> entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private Task SeedAsync(params object[] entities)
    {
        return SeedAsync(entities.AsEnumerable());
    }

    private static object[] GetGraphEntities(TenantGraph graph)
    {
        return
        [
            graph.Organization,
            graph.RecipientUser,
            graph.RecipientMembership,
            graph.Client,
            graph.LegalProcess,
            graph.LegalDeadline,
            graph.LegalTask,
            graph.CalendarEvent
        ];
    }

    private static object[] GetDeadlineGraphEntities(TenantGraph graph)
    {
        return
        [
            graph.Organization,
            graph.RecipientUser,
            graph.RecipientMembership,
            graph.Client,
            graph.LegalProcess,
            graph.LegalDeadline
        ];
    }

    private static TenantGraph CreateTenantGraph(string name, string slug)
    {
        var organization = new Organization(name, slug, CreatedAt);
        var recipientUser = new User(
            $"{name} Recipient",
            $"{slug}@example.test",
            CreatedAt);
        var recipientMembership = new OrganizationMembership(
            organization.Id,
            recipientUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        var client = new Client(
            organization.Id,
            $"{name} Client",
            CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            $"{name} Process",
            CreatedAt);
        var legalDeadline = new LegalDeadline(
            organization.Id,
            legalProcess.Id,
            $"{name} Deadline",
            DueDate,
            CreatedAt);
        var legalTask = new LegalTask(
            organization.Id,
            $"{name} Task",
            null,
            DueDate,
            legalProcess.Id,
            null,
            recipientMembership.Id,
            CreatedAt);
        var calendarEvent = new CalendarEvent(
            organization.Id,
            $"{name} Event",
            null,
            StartsAt,
            StartsAt.AddHours(1),
            null,
            null,
            null,
            null,
            recipientMembership.Id,
            CreatedAt);

        return new TenantGraph(
            organization,
            recipientUser,
            recipientMembership,
            client,
            legalProcess,
            legalDeadline,
            legalTask,
            calendarEvent);
    }

    private static Notification CreateNotification(
        NotificationKind kind,
        TenantGraph ownerGraph,
        TenantGraph? sourceGraph = null,
        DateTimeOffset? generatedAt = null)
    {
        sourceGraph ??= ownerGraph;

        return kind switch
        {
            NotificationKind.LegalDeadlineDueSoon => new Notification(
                ownerGraph.Organization.Id,
                ownerGraph.RecipientUser.Id,
                kind,
                sourceGraph.LegalDeadline.Id,
                null,
                null,
                sourceGraph.LegalDeadline.DueDate,
                null,
                generatedAt ?? GeneratedAt),
            NotificationKind.LegalTaskDueSoon => new Notification(
                ownerGraph.Organization.Id,
                ownerGraph.RecipientUser.Id,
                kind,
                null,
                sourceGraph.LegalTask.Id,
                null,
                sourceGraph.LegalTask.DueDate,
                null,
                generatedAt ?? GeneratedAt),
            NotificationKind.CalendarEventStartingSoon => new Notification(
                ownerGraph.Organization.Id,
                ownerGraph.RecipientUser.Id,
                kind,
                null,
                null,
                sourceGraph.CalendarEvent.Id,
                null,
                sourceGraph.CalendarEvent.StartsAt,
                generatedAt ?? GeneratedAt),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static void AssertPostgresException(
        DbUpdateException exception,
        string expectedSqlState,
        string expectedConstraintName)
    {
        PostgresException postgresException =
            Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(expectedSqlState, postgresException.SqlState);
        Assert.Equal(expectedConstraintName, postgresException.ConstraintName);
    }

    private static void AssertIndex(
        IEntityType entityType,
        string databaseName,
        string[] properties,
        bool unique,
        string? filter)
    {
        IIndex index = Assert.Single(
            entityType.GetIndexes(),
            candidate => candidate.GetDatabaseName() == databaseName);
        Assert.Equal(
            properties,
            index.Properties.Select(property => property.Name).ToArray());
        Assert.Equal(unique, index.IsUnique);
        Assert.Equal(filter, index.GetFilter());
    }

    private static void AssertForeignKey(
        IEntityType entityType,
        string constraintName,
        Type principalType,
        string[] properties,
        string[] principalProperties,
        DeleteBehavior deleteBehavior)
    {
        IForeignKey foreignKey = Assert.Single(
            entityType.GetForeignKeys(),
            candidate => candidate.GetConstraintName() == constraintName);
        Assert.Equal(principalType, foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(deleteBehavior, foreignKey.DeleteBehavior);
        Assert.Equal(
            properties,
            foreignKey.Properties.Select(property => property.Name).ToArray());
        Assert.Equal(
            principalProperties,
            foreignKey.PrincipalKey.Properties
                .Select(property => property.Name)
                .ToArray());
    }

    private async Task<string?> GetTableColumnsAsync(string tableName)
    {
        return await ExecuteScalarStringAsync(
            """
            SELECT string_agg(column_name, ',' ORDER BY ordinal_position)
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = @tableName
            """,
            ("tableName", tableName));
    }

    private async Task<string[]> GetCheckConstraintNamesAsync()
    {
        return await ExecuteStringArrayAsync(
            """
            SELECT constraint_name
            FROM information_schema.table_constraints
            WHERE constraint_schema = 'public'
              AND table_name = 'notifications'
              AND constraint_type = 'CHECK'
              AND constraint_name LIKE 'ck_notifications_%'
            ORDER BY constraint_name
            """);
    }

    private async Task<string[]> GetIndexNamesAsync(string tableName)
    {
        return await ExecuteStringArrayAsync(
            """
            SELECT indexname
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = @tableName
            ORDER BY indexname
            """,
            ("tableName", tableName));
    }

    private Task<string?> GetConstraintColumnsAsync(
        string tableName,
        string constraintName)
    {
        return ExecuteScalarStringAsync(
            """
            SELECT string_agg(kcu.column_name, ',' ORDER BY kcu.ordinal_position)
            FROM information_schema.table_constraints AS tc
            INNER JOIN information_schema.key_column_usage AS kcu
                ON kcu.constraint_schema = tc.constraint_schema
                AND kcu.constraint_name = tc.constraint_name
            WHERE tc.constraint_schema = 'public'
              AND tc.table_name = @tableName
              AND tc.constraint_name = @constraintName
              AND tc.constraint_type = 'FOREIGN KEY'
            """,
            ("tableName", tableName),
            ("constraintName", constraintName));
    }

    private Task<string?> GetUniqueConstraintColumnsAsync(
        string tableName,
        string constraintName)
    {
        return ExecuteScalarStringAsync(
            """
            SELECT string_agg(kcu.column_name, ',' ORDER BY kcu.ordinal_position)
            FROM information_schema.table_constraints AS tc
            INNER JOIN information_schema.key_column_usage AS kcu
                ON kcu.constraint_schema = tc.constraint_schema
                AND kcu.constraint_name = tc.constraint_name
            WHERE tc.constraint_schema = 'public'
              AND tc.table_name = @tableName
              AND tc.constraint_name = @constraintName
              AND tc.constraint_type = 'UNIQUE'
            """,
            ("tableName", tableName),
            ("constraintName", constraintName));
    }

    private Task<string?> GetDeleteRuleAsync(string constraintName)
    {
        return ExecuteScalarStringAsync(
            """
            SELECT delete_rule
            FROM information_schema.referential_constraints
            WHERE constraint_schema = 'public'
              AND constraint_name = @constraintName
            """,
            ("constraintName", constraintName));
    }

    private async Task AssertIndexDefinitionAsync(
        string tableName,
        string indexName,
        string expectedColumns,
        string? expectedPredicate)
    {
        string? indexDefinition = await ExecuteScalarStringAsync(
            """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = @tableName
              AND indexname = @indexName
            """,
            ("tableName", tableName),
            ("indexName", indexName));

        Assert.NotNull(indexDefinition);
        Assert.Contains(expectedColumns, indexDefinition);

        if (expectedPredicate is not null)
        {
            Assert.Contains(expectedPredicate, indexDefinition);
        }
    }

    private async Task<int> CountIndexesByColumnsAsync(
        string tableName,
        string expectedColumns)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT COUNT(*)::integer
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = @tableName
              AND indexdef LIKE @columnsPattern
            """,
            connection);
        command.Parameters.AddWithValue("tableName", tableName);
        command.Parameters.AddWithValue("columnsPattern", $"%{expectedColumns}%");
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string?> ExecuteScalarStringAsync(
        string query,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(query, connection);

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        object? result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (string)result;
    }

    private async Task<string[]> ExecuteStringArrayAsync(
        string query,
        params (string Name, object Value)[] parameters)
    {
        var values = new List<string>();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(query, connection);

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values.ToArray();
    }

    private static int PropertyOrder(string propertyName)
    {
        return Array.IndexOf(
            [
                nameof(Notification.Id),
                nameof(Notification.OrganizationId),
                nameof(Notification.RecipientUserId),
                nameof(Notification.Kind),
                nameof(Notification.LegalDeadlineId),
                nameof(Notification.LegalTaskId),
                nameof(Notification.CalendarEventId),
                nameof(Notification.OccurrenceDate),
                nameof(Notification.OccurrenceAt),
                nameof(Notification.GeneratedAt),
                nameof(Notification.ReadAt)
            ],
            propertyName);
    }

    private static async Task DrainTaskAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (DbUpdateException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<DbUpdateException> CaptureDbUpdateExceptionAsync(
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (DbUpdateException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(
            "The competing notification insert unexpectedly succeeded.");
    }

    private sealed record TenantGraph(
        Organization Organization,
        User RecipientUser,
        OrganizationMembership RecipientMembership,
        Client Client,
        LegalProcess LegalProcess,
        LegalDeadline LegalDeadline,
        LegalTask LegalTask,
        CalendarEvent CalendarEvent);
}
