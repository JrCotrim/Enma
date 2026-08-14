namespace Enma.Application.Tasks;

public interface ILegalTaskReadQueries
{
    Task<LegalTaskDetailReadModel?> FindAsync(
        Guid legalTaskId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<LegalTaskListReadPage> ListAsync(
        LegalTaskListReadRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record LegalTaskListReadRequest(
    Guid OrganizationId,
    LegalTaskState State,
    Guid? ProcessId,
    LegalTaskReadAssigneeFilterKind AssigneeFilterKind,
    Guid? AssigneeMembershipId,
    int PageNumber,
    int PageSize);

public enum LegalTaskReadAssigneeFilterKind
{
    Any = 0,
    Unassigned = 1,
    Membership = 2
}

public sealed record LegalTaskListReadPage(
    IReadOnlyList<LegalTaskListItem> Items,
    bool HasNext);
