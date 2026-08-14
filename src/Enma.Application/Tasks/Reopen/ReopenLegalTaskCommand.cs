namespace Enma.Application.Tasks.Reopen;

public sealed record ReopenLegalTaskCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid TaskId);
