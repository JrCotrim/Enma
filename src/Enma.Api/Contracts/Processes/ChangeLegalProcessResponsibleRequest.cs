namespace Enma.Api.Contracts.Processes;

public sealed class ChangeLegalProcessResponsibleRequest
{
    public required Guid? ResponsibleMembershipId { get; init; }
}
