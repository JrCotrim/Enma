using Enma.Application.Notifications;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Enma.Infrastructure.Persistence;

public sealed class NotificationGenerationPersistence(EnmaDbContext dbContext)
    : INotificationGenerationPersistence
{
    internal const int BatchSize = 500;
    internal const int MaximumBatchesPerSource = 10;

    // NOT EXISTS removes completed dedupe identities before LIMIT, so each
    // successful batch advances without OFFSET. The targeted ON CONFLICT is
    // still required for candidates inserted concurrently after that check.

    private const string DeadlineInsertSql =
        """
        WITH candidates AS MATERIALIZED (
            SELECT
                deadline.organization_id,
                deadline.id AS legal_deadline_id,
                membership.user_id AS recipient_user_id,
                deadline.due_date AS occurrence_date
            FROM legal_deadlines AS deadline
            INNER JOIN organizations AS organization
                ON organization.id = deadline.organization_id
                AND organization.is_active
            INNER JOIN organization_memberships AS membership
                ON membership.organization_id = deadline.organization_id
                AND membership.is_active
                AND membership.role IN (1, 2)
            INNER JOIN users AS recipient
                ON recipient.id = membership.user_id
                AND recipient.is_active
            WHERE deadline.completed_at IS NULL
              AND deadline.due_date >= @schedulerDate
              AND deadline.due_date <= @reminderWindowEnd
              AND NOT EXISTS (
                  SELECT 1
                  FROM notifications AS existing
                  WHERE existing.organization_id = deadline.organization_id
                    AND existing.legal_deadline_id = deadline.id
                    AND existing.recipient_user_id = membership.user_id
                    AND existing.kind = 1
                    AND existing.occurrence_date = deadline.due_date
              )
            ORDER BY
                deadline.due_date,
                deadline.organization_id,
                deadline.id,
                membership.user_id
            LIMIT @batchSize
        )
        INSERT INTO notifications (
            id,
            organization_id,
            recipient_user_id,
            kind,
            legal_deadline_id,
            legal_task_id,
            calendar_event_id,
            occurrence_date,
            occurrence_at,
            generated_at,
            read_at
        )
        SELECT
            gen_random_uuid(),
            candidate.organization_id,
            candidate.recipient_user_id,
            1,
            candidate.legal_deadline_id,
            NULL,
            NULL,
            candidate.occurrence_date,
            NULL,
            @generatedAt,
            NULL
        FROM candidates AS candidate
        ON CONFLICT (
            organization_id,
            legal_deadline_id,
            recipient_user_id,
            kind,
            occurrence_date
        )
        WHERE legal_deadline_id IS NOT NULL
        DO NOTHING
        """;

    private const string LegalTaskInsertSql =
        """
        WITH candidates AS MATERIALIZED (
            SELECT
                legal_task.organization_id,
                legal_task.id AS legal_task_id,
                membership.user_id AS recipient_user_id,
                legal_task.due_date AS occurrence_date
            FROM legal_tasks AS legal_task
            INNER JOIN organizations AS organization
                ON organization.id = legal_task.organization_id
                AND organization.is_active
            INNER JOIN organization_memberships AS membership
                ON membership.organization_id = legal_task.organization_id
                AND membership.id = COALESCE(
                    legal_task.assignee_membership_id,
                    legal_task.created_by_membership_id)
                AND membership.is_active
            INNER JOIN users AS recipient
                ON recipient.id = membership.user_id
                AND recipient.is_active
            WHERE legal_task.completed_at IS NULL
              AND legal_task.due_date IS NOT NULL
              AND legal_task.due_date >= @schedulerDate
              AND legal_task.due_date <= @reminderWindowEnd
              AND NOT EXISTS (
                  SELECT 1
                  FROM notifications AS existing
                  WHERE existing.organization_id = legal_task.organization_id
                    AND existing.legal_task_id = legal_task.id
                    AND existing.recipient_user_id = membership.user_id
                    AND existing.kind = 2
                    AND existing.occurrence_date = legal_task.due_date
              )
            ORDER BY
                legal_task.due_date,
                legal_task.organization_id,
                legal_task.id,
                membership.user_id
            LIMIT @batchSize
        )
        INSERT INTO notifications (
            id,
            organization_id,
            recipient_user_id,
            kind,
            legal_deadline_id,
            legal_task_id,
            calendar_event_id,
            occurrence_date,
            occurrence_at,
            generated_at,
            read_at
        )
        SELECT
            gen_random_uuid(),
            candidate.organization_id,
            candidate.recipient_user_id,
            2,
            NULL,
            candidate.legal_task_id,
            NULL,
            candidate.occurrence_date,
            NULL,
            @generatedAt,
            NULL
        FROM candidates AS candidate
        ON CONFLICT (
            organization_id,
            legal_task_id,
            recipient_user_id,
            kind,
            occurrence_date
        )
        WHERE legal_task_id IS NOT NULL
        DO NOTHING
        """;

    private const string CalendarEventInsertSql =
        """
        WITH candidates AS MATERIALIZED (
            SELECT
                calendar_event.organization_id,
                calendar_event.id AS calendar_event_id,
                membership.user_id AS recipient_user_id,
                calendar_event.starts_at AS occurrence_at
            FROM calendar_events AS calendar_event
            INNER JOIN organizations AS organization
                ON organization.id = calendar_event.organization_id
                AND organization.is_active
            INNER JOIN organization_memberships AS membership
                ON membership.organization_id = calendar_event.organization_id
                AND membership.id = COALESCE(
                    calendar_event.assignee_membership_id,
                    calendar_event.created_by_membership_id)
                AND membership.is_active
            INNER JOIN users AS recipient
                ON recipient.id = membership.user_id
                AND recipient.is_active
            WHERE calendar_event.starts_at > @windowStart
              AND calendar_event.starts_at <= @windowEnd
              AND NOT EXISTS (
                  SELECT 1
                  FROM notifications AS existing
                  WHERE existing.organization_id = calendar_event.organization_id
                    AND existing.calendar_event_id = calendar_event.id
                    AND existing.recipient_user_id = membership.user_id
                    AND existing.kind = 3
                    AND existing.occurrence_at = calendar_event.starts_at
              )
            ORDER BY
                calendar_event.starts_at,
                calendar_event.organization_id,
                calendar_event.id,
                membership.user_id
            LIMIT @batchSize
        )
        INSERT INTO notifications (
            id,
            organization_id,
            recipient_user_id,
            kind,
            legal_deadline_id,
            legal_task_id,
            calendar_event_id,
            occurrence_date,
            occurrence_at,
            generated_at,
            read_at
        )
        SELECT
            gen_random_uuid(),
            candidate.organization_id,
            candidate.recipient_user_id,
            3,
            NULL,
            NULL,
            candidate.calendar_event_id,
            NULL,
            candidate.occurrence_at,
            @generatedAt,
            NULL
        FROM candidates AS candidate
        ON CONFLICT (
            organization_id,
            calendar_event_id,
            recipient_user_id,
            kind,
            occurrence_at
        )
        WHERE calendar_event_id IS NOT NULL
        DO NOTHING
        """;

    public Task<NotificationGenerationSourceResult>
        GenerateLegalDeadlineRemindersAsync(
            DateOnly schedulerDate,
            DateOnly reminderWindowEnd,
            DateTimeOffset generatedAt,
            CancellationToken cancellationToken)
    {
        return GenerateDateOnlySourceAsync(
            DeadlineInsertSql,
            schedulerDate,
            reminderWindowEnd,
            generatedAt,
            cancellationToken);
    }

    public Task<NotificationGenerationSourceResult>
        GenerateLegalTaskRemindersAsync(
            DateOnly schedulerDate,
            DateOnly reminderWindowEnd,
            DateTimeOffset generatedAt,
            CancellationToken cancellationToken)
    {
        return GenerateDateOnlySourceAsync(
            LegalTaskInsertSql,
            schedulerDate,
            reminderWindowEnd,
            generatedAt,
            cancellationToken);
    }

    public Task<NotificationGenerationSourceResult>
        GenerateCalendarEventRemindersAsync(
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            DateTimeOffset generatedAt,
            CancellationToken cancellationToken)
    {
        return GenerateSourceAsync(
            () => dbContext.Database.ExecuteSqlRawAsync(
                CalendarEventInsertSql,
                [
                    CreateTimestampParameter("windowStart", windowStart),
                    CreateTimestampParameter("windowEnd", windowEnd),
                    CreateTimestampParameter("generatedAt", generatedAt),
                    CreateBatchSizeParameter()
                ],
                cancellationToken),
            cancellationToken);
    }

    private Task<NotificationGenerationSourceResult> GenerateDateOnlySourceAsync(
        string sql,
        DateOnly schedulerDate,
        DateOnly reminderWindowEnd,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        return GenerateSourceAsync(
            () => dbContext.Database.ExecuteSqlRawAsync(
                sql,
                [
                    CreateDateParameter("schedulerDate", schedulerDate),
                    CreateDateParameter("reminderWindowEnd", reminderWindowEnd),
                    CreateTimestampParameter("generatedAt", generatedAt),
                    CreateBatchSizeParameter()
                ],
                cancellationToken),
            cancellationToken);
    }

    private static async Task<NotificationGenerationSourceResult> GenerateSourceAsync(
        Func<Task<int>> insertBatch,
        CancellationToken cancellationToken)
    {
        int insertedCount = 0;
        int batchCount = 0;

        try
        {
            for (int batch = 0; batch < MaximumBatchesPerSource; batch++)
            {
                int insertedInBatch = await insertBatch();
                insertedCount += insertedInBatch;
                batchCount++;

                if (insertedInBatch < BatchSize)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new NotificationGenerationTransientException(
                "database-timeout",
                exception);
        }
        catch (TimeoutException exception)
        {
            throw new NotificationGenerationTransientException(
                "database-timeout",
                exception);
        }
        catch (NpgsqlException exception) when (exception.IsTransient)
        {
            throw new NotificationGenerationTransientException(
                GetTransientClassificationCode(exception),
                exception);
        }

        return new NotificationGenerationSourceResult(insertedCount, batchCount);
    }

    private static string GetTransientClassificationCode(NpgsqlException exception)
    {
        return exception is PostgresException postgresException
            ? $"postgres-{postgresException.SqlState}"
            : "npgsql-transient";
    }

    private static NpgsqlParameter<DateOnly> CreateDateParameter(
        string name,
        DateOnly value)
    {
        return new NpgsqlParameter<DateOnly>(name, NpgsqlDbType.Date)
        {
            TypedValue = value
        };
    }

    private static NpgsqlParameter<DateTimeOffset> CreateTimestampParameter(
        string name,
        DateTimeOffset value)
    {
        return new NpgsqlParameter<DateTimeOffset>(
            name,
            NpgsqlDbType.TimestampTz)
        {
            TypedValue = value.ToUniversalTime()
        };
    }

    private static NpgsqlParameter<int> CreateBatchSizeParameter()
    {
        return new NpgsqlParameter<int>("batchSize", NpgsqlDbType.Integer)
        {
            TypedValue = BatchSize
        };
    }
}
