namespace Enma.Api.Contracts.CalendarEvents;

public sealed class CreateCalendarEventRequest
{
    public required string Title { get; init; }

    public string? Description { get; init; }

    public required DateTimeOffset StartsAt { get; init; }

    public required DateTimeOffset EndsAt { get; init; }

    public string? Location { get; init; }

    public Guid? ClientId { get; init; }

    public Guid? ProcessId { get; init; }

    public Guid? AssigneeMembershipId { get; init; }
}
