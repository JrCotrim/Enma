namespace Enma.Api.Contracts.Tasks;

public sealed class ChangeLegalTaskAssigneeRequest
{
    public required Guid? AssigneeMembershipId { get; init; }
}
