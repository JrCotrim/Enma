namespace Enma.Application.Processes.Responsible;

public sealed record ChangeLegalProcessResponsibleCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid ProcessId,
    Guid? ResponsibleMembershipId);
