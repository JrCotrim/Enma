namespace Enma.Application.Processes.Details;

public sealed record ChangeLegalProcessDetailsCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid ProcessId,
    string? ProcessNumber,
    string? CourtOrAuthority);
