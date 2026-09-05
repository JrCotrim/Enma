namespace Enma.Api.Contracts.Clients;

public sealed record ListClientsResponse(
    IReadOnlyList<ClientSummaryResponse> Items,
    int PageNumber,
    int PageSize);