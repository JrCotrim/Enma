namespace Enma.Api.Contracts.Tasks;

public sealed record ListLegalTasksResponse(
    IReadOnlyList<LegalTaskListItemResponse> Items,
    int PageNumber,
    int PageSize,
    bool HasNext);
