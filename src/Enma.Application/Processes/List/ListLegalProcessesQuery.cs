namespace Enma.Application.Processes.List;

public sealed record ListLegalProcessesQuery(
    Guid UserId,
    Guid OrganizationId,
    string? Search = null,
    string? Status = null,
    string? Responsible = null,
    string? Sort = null,
    int PageNumber = 1,
    int PageSize = ListLegalProcessesUseCase.DefaultPageSize);
