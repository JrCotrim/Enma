using Enma.Domain.Processes;

namespace Enma.Application.Processes;

public sealed record LegalProcessReadModel(
    Guid Id,
    string Title,
    Guid ClientId,
    string ClientName,
    DateTimeOffset CreatedAt,
    string? ProcessNumber,
    LegalProcessStatus Status,
    string? CourtOrAuthority,
    Guid? ResponsibleMembershipId,
    string? ResponsibleDisplayName);
