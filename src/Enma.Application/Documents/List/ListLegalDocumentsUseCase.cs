using Enma.Application.Authorization;

namespace Enma.Application.Documents.List;

public sealed class ListLegalDocumentsUseCase
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;
    public const int MaximumSearchLength = 150;

    private readonly LegalDocumentReadAuthorization _readAuthorization;
    private readonly ILegalDocumentReadQueries _readQueries;

    public ListLegalDocumentsUseCase(
        LegalDocumentReadAuthorization readAuthorization,
        ILegalDocumentReadQueries readQueries)
    {
        ArgumentNullException.ThrowIfNull(readAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);

        _readAuthorization = readAuthorization;
        _readQueries = readQueries;
    }

    public async Task<ListLegalDocumentsResult> ExecuteAsync(
        ListLegalDocumentsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        LegalDocumentReadAuthorizationResult authorization =
            await _readAuthorization.AuthorizeAsync(
                query.UserId,
                query.OrganizationId,
                LegalDocumentReadAction.ListMetadata,
                cancellationToken);

        if (authorization == LegalDocumentReadAuthorizationResult.Denied)
        {
            return ListLegalDocumentsResult.AccessDenied;
        }

        if (!TryValidateAndNormalize(
                query,
                out string? normalizedFileNameSearch))
        {
            return ListLegalDocumentsResult.InvalidInput;
        }

        var request = new LegalDocumentListReadRequest(
            query.OrganizationId,
            normalizedFileNameSearch,
            query.ProcessId,
            query.ClientId,
            query.PageNumber,
            query.PageSize);

        LegalDocumentListReadPage page = await _readQueries.ListAsync(
            request,
            cancellationToken);

        return ListLegalDocumentsResult.Succeeded(
            page.Items,
            query.PageNumber,
            query.PageSize,
            page.HasNext);
    }

    private static bool TryValidateAndNormalize(
        ListLegalDocumentsQuery query,
        out string? normalizedFileNameSearch)
    {
        normalizedFileNameSearch = null;

        if (query.FileNameSearch?.Length > MaximumSearchLength ||
            query.ProcessId == Guid.Empty ||
            query.ClientId == Guid.Empty ||
            query.ProcessId.HasValue && query.ClientId.HasValue ||
            query.PageNumber <= 0 ||
            query.PageSize <= 0 ||
            query.PageSize > MaximumPageSize ||
            ((long)query.PageNumber - 1) * query.PageSize > int.MaxValue)
        {
            return false;
        }

        string? trimmedSearch = query.FileNameSearch?.Trim();
        normalizedFileNameSearch = string.IsNullOrEmpty(trimmedSearch)
            ? null
            : trimmedSearch;
        return true;
    }
}
