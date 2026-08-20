namespace Enma.Application.Documents.List;

public sealed record ListLegalDocumentsQuery(
    Guid UserId,
    Guid OrganizationId,
    string? FileNameSearch = null,
    Guid? ProcessId = null,
    Guid? ClientId = null,
    int PageNumber = 1,
    int PageSize = ListLegalDocumentsUseCase.DefaultPageSize);
