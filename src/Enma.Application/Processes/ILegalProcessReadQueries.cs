namespace Enma.Application.Processes;

public interface ILegalProcessReadQueries
{
    Task<LegalProcessReadModel?> FindAsync(
        Guid processId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LegalProcessReadModel>> ListAsync(
        Guid organizationId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);
}
