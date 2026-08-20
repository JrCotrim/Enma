using Enma.Application.Authorization;

namespace Enma.Application.Documents.GetById;

public sealed class GetLegalDocumentUseCase
{
    private readonly LegalDocumentReadAuthorization _readAuthorization;
    private readonly ILegalDocumentReadQueries _readQueries;

    public GetLegalDocumentUseCase(
        LegalDocumentReadAuthorization readAuthorization,
        ILegalDocumentReadQueries readQueries)
    {
        ArgumentNullException.ThrowIfNull(readAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);

        _readAuthorization = readAuthorization;
        _readQueries = readQueries;
    }

    public async Task<GetLegalDocumentResult> ExecuteAsync(
        GetLegalDocumentQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        LegalDocumentReadAuthorizationResult authorization =
            await _readAuthorization.AuthorizeAsync(
                query.UserId,
                query.OrganizationId,
                LegalDocumentReadAction.ViewMetadata,
                cancellationToken);

        if (authorization == LegalDocumentReadAuthorizationResult.Denied)
        {
            return GetLegalDocumentResult.AccessDenied;
        }

        if (query.DocumentId == Guid.Empty)
        {
            return GetLegalDocumentResult.InvalidInput;
        }

        LegalDocumentMetadataReadModel? document =
            await _readQueries.FindAsync(
                query.DocumentId,
                query.OrganizationId,
                cancellationToken);

        return document is null
            ? GetLegalDocumentResult.NotFound
            : GetLegalDocumentResult.Succeeded(document);
    }
}
