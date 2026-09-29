namespace Enma.Application.Processes.Create;

public sealed record CreateLegalProcessCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid ClientId,
    string Title,
    string? ProcessNumber = null,
    string? Status = null,
    string? CourtOrAuthority = null,
    Guid? ResponsibleMembershipId = null);
