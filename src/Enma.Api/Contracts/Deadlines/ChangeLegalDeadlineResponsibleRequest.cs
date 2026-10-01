namespace Enma.Api.Contracts.Deadlines;

public sealed class ChangeLegalDeadlineResponsibleRequest
{
    public required Guid? ResponsibleMembershipId { get; init; }
}
