namespace Enma.Application.Documents.GetById;

public sealed record GetLegalDocumentQuery(
    Guid UserId,
    Guid OrganizationId,
    Guid DocumentId);
