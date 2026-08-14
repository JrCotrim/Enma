namespace Enma.Application.Tasks.Update;

public sealed record UpdateLegalTaskCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid TaskId,
    string Title,
    string? Description,
    DateOnly? DueDate,
    Guid? ProcessId);
