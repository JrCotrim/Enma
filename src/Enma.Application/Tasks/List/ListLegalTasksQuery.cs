namespace Enma.Application.Tasks.List;

public sealed record ListLegalTasksQuery(
    Guid UserId,
    Guid OrganizationId,
    LegalTaskState State = LegalTaskState.Pending,
    Guid? ProcessId = null,
    LegalTaskAssigneeFilter? Assignee = null,
    int PageNumber = 1,
    int PageSize = ListLegalTasksUseCase.DefaultPageSize);
