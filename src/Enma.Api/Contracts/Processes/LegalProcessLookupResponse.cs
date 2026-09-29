namespace Enma.Api.Contracts.Processes;

public sealed record LegalProcessLookupItemResponse(
    Guid Id,
    string Title,
    string ClientName,
    string? ProcessNumber,
    LegalProcessStatusResponse Status);

public sealed record LegalProcessLookupResponse(
    IReadOnlyList<LegalProcessLookupItemResponse> Items,
    int PageNumber,
    int PageSize,
    bool HasNext);
