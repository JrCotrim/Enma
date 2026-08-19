using Enma.Domain.Organizations;

namespace Enma.Application.Documents.Upload;

public sealed record LegalDocumentUploadActorState(
    Guid UserId,
    Guid OrganizationId,
    Guid MembershipId,
    OrganizationRole Role,
    bool IsMembershipActive,
    bool IsUserActive,
    bool IsOrganizationActive);

public sealed record LegalDocumentUploadClientState(
    Guid ClientId,
    Guid OrganizationId,
    bool IsActive);

public sealed record LegalDocumentUploadProcessState(
    Guid ProcessId,
    Guid OrganizationId);

public sealed record LegalDocumentUploadLockedState(
    LegalDocumentUploadActorState? Actor,
    LegalDocumentUploadClientState? Client,
    LegalDocumentUploadProcessState? Process);
