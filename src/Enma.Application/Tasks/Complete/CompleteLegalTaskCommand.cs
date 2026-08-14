namespace Enma.Application.Tasks.Complete;

public sealed record CompleteLegalTaskCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid TaskId);
