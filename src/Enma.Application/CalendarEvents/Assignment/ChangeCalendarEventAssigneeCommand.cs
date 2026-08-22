namespace Enma.Application.CalendarEvents.Assignment;

public sealed record ChangeCalendarEventAssigneeCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid CalendarEventId,
    Guid? AssigneeMembershipId);
