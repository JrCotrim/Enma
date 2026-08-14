namespace Enma.Application.Processes.Lookup;

public sealed record LegalProcessLookupItem(
    Guid Id,
    string Title,
    string ClientName);
