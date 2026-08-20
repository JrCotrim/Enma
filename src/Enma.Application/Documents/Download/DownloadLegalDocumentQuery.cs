namespace Enma.Application.Documents.Download;

public sealed record DownloadLegalDocumentQuery(
    Guid UserId,
    Guid OrganizationId,
    Guid DocumentId);
