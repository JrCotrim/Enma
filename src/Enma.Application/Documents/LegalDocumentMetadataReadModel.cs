using Enma.Domain.Documents;

namespace Enma.Application.Documents;

public sealed record LegalDocumentMetadataReadModel(
    Guid Id,
    Guid? ClientId,
    Guid? ProcessId,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    LegalDocumentContentHash ContentHashSha256,
    Guid UploadedByMembershipId,
    DateTimeOffset CreatedAt);

public sealed record LegalDocumentListReadRequest(
    Guid OrganizationId,
    string? FileNameSearch,
    Guid? ProcessId,
    Guid? ClientId,
    int PageNumber,
    int PageSize);

public sealed record LegalDocumentListReadPage(
    IReadOnlyList<LegalDocumentMetadataReadModel> Items,
    bool HasNext);
