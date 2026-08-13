namespace Enma.Api.Contracts.Deadlines;

public sealed record ListLegalDeadlinesResponse(
    IReadOnlyList<LegalDeadlineListItemResponse> Items,
    int PageNumber,
    int PageSize);
