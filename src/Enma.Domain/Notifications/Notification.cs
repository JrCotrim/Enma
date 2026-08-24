namespace Enma.Domain.Notifications;

public sealed class Notification
{
    public Notification(
        Guid organizationId,
        Guid recipientUserId,
        NotificationKind kind,
        Guid? legalDeadlineId,
        Guid? legalTaskId,
        Guid? calendarEventId,
        DateOnly? occurrenceDate,
        DateTimeOffset? occurrenceAt,
        DateTimeOffset generatedAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                NotificationErrors.OrganizationIdRequired,
                nameof(organizationId));
        }

        if (recipientUserId == Guid.Empty)
        {
            throw new ArgumentException(
                NotificationErrors.RecipientUserIdRequired,
                nameof(recipientUserId));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                NotificationErrors.KindInvalid);
        }

        ValidateOptionalIdentifier(
            legalDeadlineId,
            nameof(legalDeadlineId),
            NotificationErrors.LegalDeadlineIdInvalid);
        ValidateOptionalIdentifier(
            legalTaskId,
            nameof(legalTaskId),
            NotificationErrors.LegalTaskIdInvalid);
        ValidateOptionalIdentifier(
            calendarEventId,
            nameof(calendarEventId),
            NotificationErrors.CalendarEventIdInvalid);

        if (occurrenceDate == DateOnly.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(occurrenceDate),
                NotificationErrors.OccurrenceDateInvalid);
        }

        if (occurrenceAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(occurrenceAt),
                NotificationErrors.OccurrenceAtInvalid);
        }

        if (generatedAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generatedAt),
                NotificationErrors.GeneratedAtInvalid);
        }

        ValidateSourceCount(legalDeadlineId, legalTaskId, calendarEventId);
        ValidateKindShape(
            kind,
            legalDeadlineId,
            legalTaskId,
            calendarEventId,
            occurrenceDate,
            occurrenceAt);

        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        RecipientUserId = recipientUserId;
        Kind = kind;
        LegalDeadlineId = legalDeadlineId;
        LegalTaskId = legalTaskId;
        CalendarEventId = calendarEventId;
        OccurrenceDate = occurrenceDate;
        OccurrenceAt = occurrenceAt?.ToUniversalTime();
        GeneratedAt = generatedAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid RecipientUserId { get; private set; }

    public NotificationKind Kind { get; private set; }

    public Guid? LegalDeadlineId { get; private set; }

    public Guid? LegalTaskId { get; private set; }

    public Guid? CalendarEventId { get; private set; }

    public DateOnly? OccurrenceDate { get; private set; }

    public DateTimeOffset? OccurrenceAt { get; private set; }

    public DateTimeOffset GeneratedAt { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public void MarkAsRead(DateTimeOffset readAt)
    {
        if (readAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readAt),
                NotificationErrors.ReadAtInvalid);
        }

        DateTimeOffset normalizedReadAt = readAt.ToUniversalTime();

        if (normalizedReadAt < GeneratedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readAt),
                NotificationErrors.ReadAtInvalid);
        }

        ReadAt ??= normalizedReadAt;
    }

    private static void ValidateOptionalIdentifier(
        Guid? identifier,
        string parameterName,
        string error)
    {
        if (identifier == Guid.Empty)
        {
            throw new ArgumentException(error, parameterName);
        }
    }

    private static void ValidateSourceCount(
        Guid? legalDeadlineId,
        Guid? legalTaskId,
        Guid? calendarEventId)
    {
        int sourceCount = Convert.ToInt32(legalDeadlineId.HasValue) +
            Convert.ToInt32(legalTaskId.HasValue) +
            Convert.ToInt32(calendarEventId.HasValue);

        if (sourceCount != 1)
        {
            throw new ArgumentException(NotificationErrors.SourceInvalid);
        }
    }

    private static void ValidateKindShape(
        NotificationKind kind,
        Guid? legalDeadlineId,
        Guid? legalTaskId,
        Guid? calendarEventId,
        DateOnly? occurrenceDate,
        DateTimeOffset? occurrenceAt)
    {
        bool sourceMatchesKind = kind switch
        {
            NotificationKind.LegalDeadlineDueSoon => legalDeadlineId.HasValue,
            NotificationKind.LegalTaskDueSoon => legalTaskId.HasValue,
            NotificationKind.CalendarEventStartingSoon => calendarEventId.HasValue,
            _ => false
        };

        if (!sourceMatchesKind)
        {
            throw new ArgumentException(
                NotificationErrors.KindSourceMismatch,
                nameof(kind));
        }

        if (kind is NotificationKind.LegalDeadlineDueSoon or
            NotificationKind.LegalTaskDueSoon)
        {
            if (!occurrenceDate.HasValue)
            {
                throw new ArgumentException(
                    NotificationErrors.OccurrenceDateRequired,
                    nameof(occurrenceDate));
            }

            if (occurrenceAt.HasValue)
            {
                throw new ArgumentException(
                    NotificationErrors.OccurrenceAtMustBeNull,
                    nameof(occurrenceAt));
            }

            return;
        }

        if (occurrenceDate.HasValue)
        {
            throw new ArgumentException(
                NotificationErrors.OccurrenceDateMustBeNull,
                nameof(occurrenceDate));
        }

        if (!occurrenceAt.HasValue)
        {
            throw new ArgumentException(
                NotificationErrors.OccurrenceAtRequired,
                nameof(occurrenceAt));
        }
    }
}
