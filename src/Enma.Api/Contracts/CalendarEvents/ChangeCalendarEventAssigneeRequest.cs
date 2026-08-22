namespace Enma.Api.Contracts.CalendarEvents;

public sealed class ChangeCalendarEventAssigneeRequest
{
    public required Guid? AssigneeMembershipId { get; init; }
}
