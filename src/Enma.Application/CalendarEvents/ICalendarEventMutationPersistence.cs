using Enma.Domain.CalendarEvents;

namespace Enma.Application.CalendarEvents;

public interface ICalendarEventMutationPersistence
{
    Task<CalendarEventMutationPersistenceResult> ExecuteAsync(
        CalendarEventMutationPersistenceRequest request,
        Func<CalendarEventMutationLockedState, CalendarEventMutationDecision> decide,
        CancellationToken cancellationToken = default);
}

public sealed record CalendarEventMutationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid CalendarEventId);

public sealed record CalendarEventMutationLockedState(
    CalendarEvent CalendarEvent,
    bool IsOrganizationActive,
    CalendarEventMemberState? Actor,
    bool AssociationLookupPerformed,
    Guid? ValidatedClientId,
    bool? IsClientAvailable,
    Guid? ValidatedProcessId,
    bool? IsProcessAvailable,
    bool AssigneeLookupPerformed,
    Guid? ValidatedAssigneeMembershipId,
    CalendarEventMemberState? Assignee);

public sealed class CalendarEventMutationDecision
{
    private CalendarEventMutationDecision(
        CalendarEventMutationDecisionStatus status,
        Guid? clientId,
        Guid? processId,
        Guid? assigneeMembershipId)
    {
        Status = status;
        ClientId = clientId;
        ProcessId = processId;
        AssigneeMembershipId = assigneeMembershipId;
    }

    public CalendarEventMutationDecisionStatus Status { get; }

    public Guid? ClientId { get; }

    public Guid? ProcessId { get; }

    public Guid? AssigneeMembershipId { get; }

    public static CalendarEventMutationDecision AccessDenied { get; } = new(
        CalendarEventMutationDecisionStatus.AccessDenied,
        null,
        null,
        null);

    public static CalendarEventMutationDecision RelatedClientUnavailable { get; } =
        new(
            CalendarEventMutationDecisionStatus.RelatedClientUnavailable,
            null,
            null,
            null);

    public static CalendarEventMutationDecision RelatedProcessUnavailable { get; } =
        new(
            CalendarEventMutationDecisionStatus.RelatedProcessUnavailable,
            null,
            null,
            null);

    public static CalendarEventMutationDecision RelatedAssigneeUnavailable { get; } =
        new(
            CalendarEventMutationDecisionStatus.RelatedAssigneeUnavailable,
            null,
            null,
            null);

    public static CalendarEventMutationDecision InvalidInput { get; } = new(
        CalendarEventMutationDecisionStatus.InvalidInput,
        null,
        null,
        null);

    public static CalendarEventMutationDecision Persist { get; } = new(
        CalendarEventMutationDecisionStatus.Persist,
        null,
        null,
        null);

    public static CalendarEventMutationDecision Delete { get; } = new(
        CalendarEventMutationDecisionStatus.Delete,
        null,
        null,
        null);

    public static CalendarEventMutationDecision ValidateAssociation(
        Guid? clientId,
        Guid? processId)
    {
        if (clientId == Guid.Empty ||
            processId == Guid.Empty ||
            clientId is not null && processId is not null ||
            clientId is null && processId is null)
        {
            throw new ArgumentException(
                "Exactly one valid calendar event association must be supplied.");
        }

        return new CalendarEventMutationDecision(
            CalendarEventMutationDecisionStatus.ValidateAssociation,
            clientId,
            processId,
            null);
    }

    public static CalendarEventMutationDecision ValidateAssignee(
        Guid assigneeMembershipId)
    {
        if (assigneeMembershipId == Guid.Empty)
        {
            throw new ArgumentException(
                "Assignee membership id cannot be empty.",
                nameof(assigneeMembershipId));
        }

        return new CalendarEventMutationDecision(
            CalendarEventMutationDecisionStatus.ValidateAssignee,
            null,
            null,
            assigneeMembershipId);
    }
}

public enum CalendarEventMutationDecisionStatus
{
    AccessDenied = 0,
    RelatedClientUnavailable = 1,
    RelatedProcessUnavailable = 2,
    RelatedAssigneeUnavailable = 3,
    InvalidInput = 4,
    ValidateAssociation = 5,
    ValidateAssignee = 6,
    Persist = 7,
    Delete = 8
}

public enum CalendarEventMutationPersistenceResult
{
    AccessDenied = 0,
    NotFound = 1,
    RelatedClientUnavailable = 2,
    RelatedProcessUnavailable = 3,
    RelatedAssigneeUnavailable = 4,
    InvalidInput = 5,
    Succeeded = 6,
    Deleted = 7
}
