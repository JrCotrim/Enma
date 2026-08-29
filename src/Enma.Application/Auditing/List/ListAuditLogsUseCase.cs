using Enma.Application.Authorization;
using Enma.Application.Validation;
using Enma.Domain.Auditing;

namespace Enma.Application.Auditing.List;

public sealed class ListAuditLogsUseCase
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;

    private readonly OrganizationAdministrationAuthorization _authorization;
    private readonly IAuditLogReadQueries _queries;

    public ListAuditLogsUseCase(
        OrganizationAdministrationAuthorization authorization,
        IAuditLogReadQueries queries)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(queries);
        _authorization = authorization;
        _queries = queries;
    }

    public async Task<ListAuditLogsResult> ExecuteAsync(
        ListAuditLogsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        AuditEventType? eventType = ParseEventType(query.EventType);
        AuditEntityType? entityType = ParseEntityType(query.EntityType);
        ValidateEntityFilter(entityType, query.EntityId);
        ValidatePagination(query.PageNumber, query.PageSize);

        OrganizationAdministrationAuthorizationResult authorization =
            await _authorization.AuthorizeAsync(
                query.UserId,
                query.OrganizationId,
                cancellationToken);

        if (!authorization.Allows(OrganizationAdministrationAction.ViewAuditLog) ||
            authorization.OrganizationId is not Guid authorizedOrganizationId)
        {
            return ListAuditLogsResult.AccessDenied;
        }

        AuditLogReadPage page = await _queries.ListAsync(
            new AuditLogReadQuery(
                authorizedOrganizationId,
                eventType,
                entityType,
                query.EntityId,
                query.PageNumber,
                query.PageSize),
            cancellationToken);

        return ListAuditLogsResult.Success(
            page,
            query.PageNumber,
            query.PageSize);
    }

    private static AuditEventType? ParseEventType(string? value)
    {
        string? normalized = Normalize(value);

        if (normalized is null)
        {
            return null;
        }

        foreach (AuditEventType candidate in Enum.GetValues<AuditEventType>())
        {
            if (StringComparer.Ordinal.Equals(candidate.ToCode(), normalized))
            {
                return candidate;
            }
        }

        throw new RequestValidationException("Event type is invalid.");
    }

    private static AuditEntityType? ParseEntityType(string? value)
    {
        string? normalized = Normalize(value);

        if (normalized is null)
        {
            return null;
        }

        foreach (AuditEntityType candidate in Enum.GetValues<AuditEntityType>())
        {
            if (StringComparer.Ordinal.Equals(candidate.ToCode(), normalized))
            {
                return candidate;
            }
        }

        throw new RequestValidationException("Entity type is invalid.");
    }

    private static string? Normalize(string? value)
    {
        string? normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static void ValidateEntityFilter(
        AuditEntityType? entityType,
        Guid? entityId)
    {
        if (entityType.HasValue != entityId.HasValue || entityId == Guid.Empty)
        {
            throw new RequestValidationException(
                "Entity type and entity id must be provided together.");
        }
    }

    private static void ValidatePagination(int pageNumber, int pageSize)
    {
        if (pageNumber < 1)
        {
            throw new RequestValidationException(
                "Page number must be at least 1.");
        }

        if (pageSize < 1 || pageSize > MaximumPageSize)
        {
            throw new RequestValidationException(
                $"Page size must be between 1 and {MaximumPageSize}.");
        }

        if (((long)pageNumber - 1) * pageSize > int.MaxValue)
        {
            throw new RequestValidationException(
                "The requested page offset is too large.");
        }
    }
}
