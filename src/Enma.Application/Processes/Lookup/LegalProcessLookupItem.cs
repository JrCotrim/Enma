using Enma.Domain.Processes;

namespace Enma.Application.Processes.Lookup;

public sealed record LegalProcessLookupItem(
    Guid Id,
    string Title,
    string ClientName,
    string? ProcessNumber,
    LegalProcessStatus Status,
    Guid? ResponsibleMembershipId);
