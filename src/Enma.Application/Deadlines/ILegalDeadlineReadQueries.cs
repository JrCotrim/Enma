namespace Enma.Application.Deadlines;

public interface ILegalDeadlineReadQueries
{
    Task<LegalDeadlineDetailReadModel?> FindAsync(
        Guid deadlineId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LegalDeadlineListItem>> ListAsync(
        Guid organizationId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);
}
