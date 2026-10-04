using Enma.Application.Authorization;
using Enma.Application.Filtering;
using Enma.Application.Validation;

namespace Enma.Application.Deadlines.List;

public sealed class ListLegalDeadlinesUseCase
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;

    private readonly DeadlineActionAuthorization _actionAuthorization;
    private readonly ILegalDeadlineReadQueries _readQueries;

    public ListLegalDeadlinesUseCase(
        DeadlineActionAuthorization actionAuthorization,
        ILegalDeadlineReadQueries readQueries)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);

        _actionAuthorization = actionAuthorization;
        _readQueries = readQueries;
    }

    public async Task<ListLegalDeadlinesResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        int pageNumber = 1,
        int pageSize = DefaultPageSize,
        string? responsible = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePagination(pageNumber, pageSize);
        ResponsibleFilter responsibleFilter =
            ResponsibleFilter.Parse(responsible);

        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                userId,
                organizationId,
                DeadlineAction.View,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return ListLegalDeadlinesResult.AccessDenied;
        }

        (LegalDeadlineReadResponsibleFilterKind responsibleKind,
            Guid? responsibleMembershipId) = responsibleFilter.Kind switch
        {
            ResponsibleFilterKind.Any =>
                (LegalDeadlineReadResponsibleFilterKind.Any, (Guid?)null),
            ResponsibleFilterKind.Self =>
                (LegalDeadlineReadResponsibleFilterKind.Membership, actorMembershipId),
            ResponsibleFilterKind.Unassigned =>
                (LegalDeadlineReadResponsibleFilterKind.Unassigned, null),
            ResponsibleFilterKind.Membership =>
                (LegalDeadlineReadResponsibleFilterKind.Membership,
                    responsibleFilter.MembershipId),
            _ => throw new InvalidOperationException(
                "The legal deadline responsible filter is unsupported.")
        };

        IReadOnlyList<LegalDeadlineListItem> legalDeadlines =
            await _readQueries.ListAsync(
                new LegalDeadlineListReadRequest(
                    organizationId,
                    responsibleKind,
                    responsibleMembershipId,
                    pageNumber,
                    pageSize),
                cancellationToken);

        return ListLegalDeadlinesResult.Success(
            legalDeadlines,
            pageNumber,
            pageSize);
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

        long skippedItems = ((long)pageNumber - 1) * pageSize;

        if (skippedItems > int.MaxValue)
        {
            throw new RequestValidationException(
                "Pagination offset is too large.");
        }
    }
}
