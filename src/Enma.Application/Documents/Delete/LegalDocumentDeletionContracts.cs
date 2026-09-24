using Enma.Application.Documents.Storage;
using Enma.Domain.Organizations;

namespace Enma.Application.Documents.Delete;

public sealed record DeleteLegalDocumentCommand(
    Guid UserId,
    Guid OrganizationId,
    Guid DocumentId);

public sealed class DeleteLegalDocumentResult
{
    private DeleteLegalDocumentResult(DeleteLegalDocumentResultStatus status)
    {
        Status = status;
    }

    public DeleteLegalDocumentResultStatus Status { get; }

    public static DeleteLegalDocumentResult AccessDenied { get; } = new(
        DeleteLegalDocumentResultStatus.AccessDenied);

    public static DeleteLegalDocumentResult NotFound { get; } = new(
        DeleteLegalDocumentResultStatus.NotFound);

    public static DeleteLegalDocumentResult InvalidInput { get; } = new(
        DeleteLegalDocumentResultStatus.InvalidInput);

    public static DeleteLegalDocumentResult Accepted { get; } = new(
        DeleteLegalDocumentResultStatus.Accepted);
}

public enum DeleteLegalDocumentResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    InvalidInput = 2,
    Accepted = 3
}

public sealed record LegalDocumentDeletionPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid DocumentId,
    DateTimeOffset RequestedAt);

public sealed record LegalDocumentDeletionActorState(
    Guid UserId,
    Guid OrganizationId,
    Guid MembershipId,
    OrganizationRole Role,
    bool IsMembershipActive,
    bool IsUserActive,
    bool IsOrganizationActive);

public sealed record LegalDocumentDeletionDocumentState(
    Guid DocumentId,
    Guid OrganizationId,
    bool IsDeletionPending);

public sealed record LegalDocumentDeletionLockedState(
    LegalDocumentDeletionActorState? Actor,
    LegalDocumentDeletionDocumentState? Document);

public enum LegalDocumentDeletionDecision
{
    AccessDenied = 0,
    NotFound = 1,
    Accept = 2
}

public enum LegalDocumentDeletionPersistenceResult
{
    AccessDenied = 0,
    NotFound = 1,
    Accepted = 2
}

public sealed record PendingLegalDocumentDeletion(
    Guid OrganizationId,
    Guid DocumentId,
    LegalDocumentStorageObjectKey ObjectKey);

public interface ILegalDocumentDeletionPersistence
{
    Task<LegalDocumentDeletionPersistenceResult> RequestAsync(
        LegalDocumentDeletionPersistenceRequest request,
        Func<LegalDocumentDeletionLockedState, LegalDocumentDeletionDecision> decide,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PendingLegalDocumentDeletion>> ListPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);

    Task<bool> FinalizeAsync(
        PendingLegalDocumentDeletion deletion,
        CancellationToken cancellationToken = default);
}

public sealed record ProcessLegalDocumentDeletionsResult(
    int FoundCount,
    int CompletedCount,
    int DeferredCount);
