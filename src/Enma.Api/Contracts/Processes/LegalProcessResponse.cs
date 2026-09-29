namespace Enma.Api.Contracts.Processes;

public sealed record LegalProcessResponse(
    Guid Id,
    string Title,
    Guid ClientId,
    string ClientName,
    DateTimeOffset CreatedAt,
    string? ProcessNumber,
    LegalProcessStatusResponse Status,
    string? CourtOrAuthority,
    Guid? ResponsibleMembershipId,
    string? ResponsibleDisplayName);
