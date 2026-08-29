namespace Enma.Application.Auditing.List;

public sealed record ListAuditLogsQuery(
    Guid UserId,
    Guid OrganizationId,
    string? EventType = null,
    string? EntityType = null,
    Guid? EntityId = null,
    int PageNumber = 1,
    int PageSize = ListAuditLogsUseCase.DefaultPageSize);
