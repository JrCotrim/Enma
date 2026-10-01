namespace Enma.Application.Deadlines;

public interface ILegalDeadlineReadQueries
{
    Task<LegalDeadlineDetailReadModel?> FindAsync(
        Guid deadlineId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LegalDeadlineListItem>> ListAsync(
        LegalDeadlineListReadRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record LegalDeadlineListReadRequest(
    Guid OrganizationId,
    LegalDeadlineReadResponsibleFilterKind ResponsibleFilterKind,
    Guid? ResponsibleMembershipId,
    int PageNumber,
    int PageSize);

public enum LegalDeadlineReadResponsibleFilterKind
{
    Any = 0,
    Unassigned = 1,
    Membership = 2
}
