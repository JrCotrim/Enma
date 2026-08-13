namespace Enma.Api.Contracts.Processes;

public sealed record ListLegalProcessesResponse(
    IReadOnlyList<LegalProcessResponse> Items,
    int PageNumber,
    int PageSize);
