namespace Enma.Application.Processes.Status;

public sealed record ChangeLegalProcessStatusCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid ProcessId,
    string? Status);
