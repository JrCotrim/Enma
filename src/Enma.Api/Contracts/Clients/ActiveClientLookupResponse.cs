namespace Enma.Api.Contracts.Clients;

public sealed record ActiveClientLookupItemResponse(
    Guid Id,
    string Name);

public sealed record ActiveClientLookupResponse(
    IReadOnlyList<ActiveClientLookupItemResponse> Items,
    int PageNumber,
    int PageSize,
    bool HasNext);
