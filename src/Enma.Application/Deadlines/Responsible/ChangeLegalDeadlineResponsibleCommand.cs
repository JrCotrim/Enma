namespace Enma.Application.Deadlines.Responsible;

public sealed record ChangeLegalDeadlineResponsibleCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid DeadlineId,
    Guid? ResponsibleMembershipId);
