namespace Enma.Api.Contracts.Documents;

public sealed record ListLegalDocumentsResponse(
    IReadOnlyList<LegalDocumentMetadataResponse> Items,
    int PageNumber,
    int PageSize,
    bool HasNext);
