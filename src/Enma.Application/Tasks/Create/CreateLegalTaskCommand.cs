namespace Enma.Application.Tasks.Create;

public sealed record CreateLegalTaskCommand(
    Guid UserId,
    Guid OrganizationId,
    string Title,
    string? Description,
    DateOnly? DueDate,
    Guid? ProcessId,
    Guid? AssigneeMembershipId);
