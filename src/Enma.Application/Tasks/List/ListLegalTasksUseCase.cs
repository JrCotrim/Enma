using Enma.Application.Authorization;

namespace Enma.Application.Tasks.List;

public sealed class ListLegalTasksUseCase
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;

    private readonly LegalTaskViewAuthorization _viewAuthorization;
    private readonly ILegalTaskReadQueries _readQueries;

    public ListLegalTasksUseCase(
        LegalTaskViewAuthorization viewAuthorization,
        ILegalTaskReadQueries readQueries)
    {
        ArgumentNullException.ThrowIfNull(viewAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);
        _viewAuthorization = viewAuthorization;
        _readQueries = readQueries;
    }

    public async Task<ListLegalTasksResult> ExecuteAsync(
        ListLegalTasksQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        LegalTaskViewAuthorizationResult authorization =
            await _viewAuthorization.AuthorizeAsync(
                query.UserId,
                query.OrganizationId,
                cancellationToken);

        if (authorization.Status == LegalTaskViewAuthorizationStatus.Denied ||
            authorization.MembershipId is not Guid actorMembershipId)
        {
            return ListLegalTasksResult.AccessDenied;
        }

        LegalTaskAssigneeFilter assignee =
            query.Assignee ?? LegalTaskAssigneeFilter.Any;

        if (!TryValidate(
                query,
                assignee,
                out LegalTaskReadAssigneeFilterKind readAssigneeKind,
                out Guid? assigneeMembershipId))
        {
            return ListLegalTasksResult.InvalidInput;
        }

        if (assignee.Kind == LegalTaskAssigneeFilterKind.Self)
        {
            readAssigneeKind = LegalTaskReadAssigneeFilterKind.Membership;
            assigneeMembershipId = actorMembershipId;
        }

        var request = new LegalTaskListReadRequest(
            query.OrganizationId,
            query.State,
            query.ProcessId,
            readAssigneeKind,
            assigneeMembershipId,
            query.PageNumber,
            query.PageSize);

        LegalTaskListReadPage page = await _readQueries.ListAsync(
            request,
            cancellationToken);

        return ListLegalTasksResult.Succeeded(
            page.Items,
            query.PageNumber,
            query.PageSize,
            page.HasNext);
    }

    private static bool TryValidate(
        ListLegalTasksQuery query,
        LegalTaskAssigneeFilter assignee,
        out LegalTaskReadAssigneeFilterKind readKind,
        out Guid? membershipId)
    {
        readKind = LegalTaskReadAssigneeFilterKind.Any;
        membershipId = null;

        if (!Enum.IsDefined(query.State) ||
            query.ProcessId == Guid.Empty ||
            query.PageNumber <= 0 ||
            query.PageSize <= 0 ||
            query.PageSize > MaximumPageSize ||
            ((long)query.PageNumber - 1) * query.PageSize > int.MaxValue ||
            !Enum.IsDefined(assignee.Kind))
        {
            return false;
        }

        switch (assignee.Kind)
        {
            case LegalTaskAssigneeFilterKind.Any when assignee.MembershipId is null:
                readKind = LegalTaskReadAssigneeFilterKind.Any;
                return true;
            case LegalTaskAssigneeFilterKind.Self when assignee.MembershipId is null:
                readKind = LegalTaskReadAssigneeFilterKind.Membership;
                return true;
            case LegalTaskAssigneeFilterKind.Unassigned
                when assignee.MembershipId is null:
                readKind = LegalTaskReadAssigneeFilterKind.Unassigned;
                return true;
            case LegalTaskAssigneeFilterKind.Membership
                when assignee.MembershipId is Guid value && value != Guid.Empty:
                readKind = LegalTaskReadAssigneeFilterKind.Membership;
                membershipId = value;
                return true;
            default:
                return false;
        }
    }
}
