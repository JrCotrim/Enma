namespace Enma.Application.Deadlines;

public interface ILegalDeadlineMutationPersistence
{
    Task<LegalDeadlineDetailsMutationPersistenceResult> UpdateDetailsAsync(
        Guid deadlineId,
        Guid organizationId,
        string title,
        DateOnly dueDate,
        CancellationToken cancellationToken = default);

    Task<LegalDeadlineLifecycleMutationPersistenceResult> CompleteAsync(
        Guid deadlineId,
        Guid organizationId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);

    Task<LegalDeadlineLifecycleMutationPersistenceResult> ReopenAsync(
        Guid deadlineId,
        Guid organizationId,
        CancellationToken cancellationToken = default);
}

public enum LegalDeadlineDetailsMutationPersistenceResult
{
    NotFound = 0,
    Conflict = 1,
    Updated = 2
}

public enum LegalDeadlineLifecycleMutationPersistenceResult
{
    NotFound = 0,
    Succeeded = 1
}
