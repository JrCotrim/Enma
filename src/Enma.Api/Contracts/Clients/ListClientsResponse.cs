namespace Enma.Api.Contracts.Clients;

public sealed record ListClientsResponse(
    IReadOnlyList<ClientResponse> Items,
    int PageNumber,
    int PageSize);
