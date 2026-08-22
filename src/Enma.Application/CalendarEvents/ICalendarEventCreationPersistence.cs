using Enma.Domain.CalendarEvents;
using Enma.Domain.Organizations;

namespace Enma.Application.CalendarEvents;

public interface ICalendarEventCreationPersistence
{
    Task<CalendarEventCreationPersistenceResult> ExecuteAsync(
        CalendarEventCreationPersistenceRequest request,
        Func<CalendarEventCreationLockedState, CalendarEventCreationDecision> decide,
        CancellationToken cancellationToken = default);
}

public sealed record CalendarEventCreationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid? ClientId,
    Guid? ProcessId,
    Guid? AssigneeMembershipId);

public sealed record CalendarEventCreationLockedState(
    bool IsOrganizationActive,
    CalendarEventMemberState? Actor,
    CalendarEventMemberState? Assignee,
    bool? IsClientAvailable,
    bool? IsProcessAvailable);

public sealed record CalendarEventMemberState(
    Guid MembershipId,
    Guid OrganizationId,
    Guid UserId,
    OrganizationRole Role,
    bool IsMembershipActive,
    bool IsUserActive);

public sealed class CalendarEventCreationDecision
{
    private CalendarEventCreationDecision(
        CalendarEventCreationDecisionStatus status,
        CalendarEvent? calendarEvent)
    {
        Status = status;
        CalendarEvent = calendarEvent;
    }

    public CalendarEventCreationDecisionStatus Status { get; }

    public CalendarEvent? CalendarEvent { get; }

    public static CalendarEventCreationDecision AccessDenied { get; } = new(
        CalendarEventCreationDecisionStatus.AccessDenied,
        null);

    public static CalendarEventCreationDecision RelatedClientUnavailable { get; } =
        new(
            CalendarEventCreationDecisionStatus.RelatedClientUnavailable,
            null);

    public static CalendarEventCreationDecision RelatedProcessUnavailable { get; } =
        new(
            CalendarEventCreationDecisionStatus.RelatedProcessUnavailable,
            null);

    public static CalendarEventCreationDecision RelatedAssigneeUnavailable { get; } =
        new(
            CalendarEventCreationDecisionStatus.RelatedAssigneeUnavailable,
            null);

    public static CalendarEventCreationDecision InvalidInput { get; } = new(
        CalendarEventCreationDecisionStatus.InvalidInput,
        null);

    public static CalendarEventCreationDecision Persist(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        return new CalendarEventCreationDecision(
            CalendarEventCreationDecisionStatus.Persist,
            calendarEvent);
    }
}

public enum CalendarEventCreationDecisionStatus
{
    AccessDenied = 0,
    RelatedClientUnavailable = 1,
    RelatedProcessUnavailable = 2,
    RelatedAssigneeUnavailable = 3,
    InvalidInput = 4,
    Persist = 5
}

public sealed class CalendarEventCreationPersistenceResult
{
    private CalendarEventCreationPersistenceResult(
        CalendarEventCreationDecisionStatus status,
        Guid? calendarEventId)
    {
        Status = status;
        CalendarEventId = calendarEventId;
    }

    public CalendarEventCreationDecisionStatus Status { get; }

    public Guid? CalendarEventId { get; }

    public static CalendarEventCreationPersistenceResult Rejected(
        CalendarEventCreationDecisionStatus status)
    {
        if (status == CalendarEventCreationDecisionStatus.Persist ||
            !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new CalendarEventCreationPersistenceResult(status, null);
    }

    public static CalendarEventCreationPersistenceResult Created(
        Guid calendarEventId)
    {
        if (calendarEventId == Guid.Empty)
        {
            throw new ArgumentException(
                "Calendar event id cannot be empty.",
                nameof(calendarEventId));
        }

        return new CalendarEventCreationPersistenceResult(
            CalendarEventCreationDecisionStatus.Persist,
            calendarEventId);
    }
}
