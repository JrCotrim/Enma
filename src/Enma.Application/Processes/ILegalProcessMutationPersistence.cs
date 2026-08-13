namespace Enma.Application.Processes;

public interface ILegalProcessMutationPersistence
{
    Task<LegalProcessMutationPersistenceResult> UpdateTitleAsync(
        Guid processId,
        Guid organizationId,
        string title,
        CancellationToken cancellationToken = default);
}

public enum LegalProcessMutationPersistenceResult
{
    NotFound = 0,
    Updated = 1
}
