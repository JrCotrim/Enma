using Enma.Domain.Processes;

namespace Enma.Application.Processes;

public interface ILegalProcessReadQueries
{
    Task<LegalProcessReadModel?> FindAsync(
        Guid processId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns at most <see cref="LegalProcessListReadRequest.PageSize"/> + 1
    /// items; the extra item only signals that a next page exists.
    /// </summary>
    Task<IReadOnlyList<LegalProcessReadModel>> ListAsync(
        LegalProcessListReadRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record LegalProcessListReadRequest(
    Guid OrganizationId,
    string? Search,
    LegalProcessStatus? Status,
    LegalProcessReadResponsibleFilterKind ResponsibleFilterKind,
    Guid? ResponsibleMembershipId,
    LegalProcessListSort Sort,
    int PageNumber,
    int PageSize);

public enum LegalProcessReadResponsibleFilterKind
{
    Any = 0,
    Unassigned = 1,
    Membership = 2
}

public enum LegalProcessListSort
{
    Title = 0,
    Newest = 1
}
