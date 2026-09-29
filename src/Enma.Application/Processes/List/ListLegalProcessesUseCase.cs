using Enma.Application.Authorization;
using Enma.Application.Validation;
using Enma.Domain.Processes;

namespace Enma.Application.Processes.List;

public sealed class ListLegalProcessesUseCase
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;
    public const int MaximumSearchLength = 150;

    private readonly ProcessActionAuthorization _actionAuthorization;
    private readonly ILegalProcessReadQueries _readQueries;

    public ListLegalProcessesUseCase(
        ProcessActionAuthorization actionAuthorization,
        ILegalProcessReadQueries readQueries)
    {
        ArgumentNullException.ThrowIfNull(actionAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);

        _actionAuthorization = actionAuthorization;
        _readQueries = readQueries;
    }

    public async Task<ListLegalProcessesResult> ExecuteAsync(
        ListLegalProcessesQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        ValidatePagination(query.PageNumber, query.PageSize);
        string? search = NormalizeAndValidateSearch(query.Search);
        LegalProcessStatus? status = query.Status is null
            ? null
            : LegalProcessStatusParser.Parse(query.Status);
        LegalProcessResponsibleFilter responsible =
            LegalProcessResponsibleFilter.Parse(query.Responsible);
        LegalProcessListSort sort = ParseSort(query.Sort);

        OrganizationAccessAuthorizationResult authorization =
            await _actionAuthorization.AuthorizeActorAsync(
                query.UserId,
                query.OrganizationId,
                ProcessAction.View,
                cancellationToken);

        if (authorization.MembershipId is not Guid actorMembershipId)
        {
            return ListLegalProcessesResult.AccessDenied;
        }

        (LegalProcessReadResponsibleFilterKind responsibleKind,
            Guid? responsibleMembershipId) = responsible.Kind switch
        {
            LegalProcessResponsibleFilterKind.Any =>
                (LegalProcessReadResponsibleFilterKind.Any, (Guid?)null),
            LegalProcessResponsibleFilterKind.Self =>
                (LegalProcessReadResponsibleFilterKind.Membership, actorMembershipId),
            LegalProcessResponsibleFilterKind.Unassigned =>
                (LegalProcessReadResponsibleFilterKind.Unassigned, null),
            LegalProcessResponsibleFilterKind.Membership =>
                (LegalProcessReadResponsibleFilterKind.Membership,
                    responsible.MembershipId),
            _ => throw new InvalidOperationException(
                "The legal process responsible filter is unsupported.")
        };

        IReadOnlyList<LegalProcessReadModel> legalProcesses =
            await _readQueries.ListAsync(
                new LegalProcessListReadRequest(
                    query.OrganizationId,
                    search,
                    status,
                    responsibleKind,
                    responsibleMembershipId,
                    sort,
                    query.PageNumber,
                    query.PageSize),
                cancellationToken);

        return ListLegalProcessesResult.Success(
            legalProcesses,
            query.PageNumber,
            query.PageSize);
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
    }

    private static string? NormalizeAndValidateSearch(string? search)
    {
        string? normalizedSearch = search?.Trim();

        if (normalizedSearch?.Length > MaximumSearchLength)
        {
            throw new RequestValidationException(
                $"Search must not exceed {MaximumSearchLength} characters.");
        }

        return string.IsNullOrEmpty(normalizedSearch)
            ? null
            : normalizedSearch;
    }

    private static LegalProcessListSort ParseSort(string? value)
    {
        return value switch
        {
            null or "title" => LegalProcessListSort.Title,
            "newest" => LegalProcessListSort.Newest,
            _ => throw new RequestValidationException(
                "Sort must be 'title' or 'newest'.")
        };
    }
}
