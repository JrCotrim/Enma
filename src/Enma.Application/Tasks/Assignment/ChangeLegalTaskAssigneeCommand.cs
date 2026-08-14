namespace Enma.Application.Tasks.Assignment;

public sealed record ChangeLegalTaskAssigneeCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid TaskId,
    Guid? AssigneeMembershipId);
