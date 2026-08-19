using Enma.Application.Documents.Storage;
using Enma.Domain.Documents;

namespace Enma.Application.Documents.Upload;

public sealed class LegalDocumentUploadPersistenceRequest
{
    public LegalDocumentUploadPersistenceRequest(
        Guid userId,
        Guid organizationId,
        Guid actorMembershipId,
        Guid? clientId,
        Guid? processId,
        string originalFileName,
        LegalDocumentStorageObjectKey objectKey,
        string canonicalContentType,
        long contentLength,
        LegalDocumentContentHash contentHashSha256)
    {
        ArgumentNullException.ThrowIfNull(originalFileName);
        ArgumentNullException.ThrowIfNull(objectKey);
        ArgumentNullException.ThrowIfNull(canonicalContentType);
        ArgumentNullException.ThrowIfNull(contentHashSha256);

        UserId = userId;
        OrganizationId = organizationId;
        ActorMembershipId = actorMembershipId;
        ClientId = clientId;
        ProcessId = processId;
        OriginalFileName = originalFileName;
        ObjectKey = objectKey;
        CanonicalContentType = canonicalContentType;
        ContentLength = contentLength;
        ContentHashSha256 = contentHashSha256;
    }

    public Guid UserId { get; }

    public Guid OrganizationId { get; }

    public Guid ActorMembershipId { get; }

    public Guid? ClientId { get; }

    public Guid? ProcessId { get; }

    public string OriginalFileName { get; }

    public LegalDocumentStorageObjectKey ObjectKey { get; }

    public string CanonicalContentType { get; }

    public long ContentLength { get; }

    public LegalDocumentContentHash ContentHashSha256 { get; }
}
