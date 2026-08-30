using Enma.Application.Authorization;

namespace Enma.Application.Tasks.Reopen;

public sealed class ReopenLegalTaskUseCase
{
    private readonly OrganizationAccessAuthorization _organizationAccessAuthorization;
    private readonly LegalTaskMutationAuthorization _mutationAuthorization;
    private readonly ILegalTaskMutationPersistence _mutationPersistence;

    public ReopenLegalTaskUseCase(
        OrganizationAccessAuthorization organizationAccessAuthorization,
        LegalTaskMutationAuthorization mutationAuthorization,
        ILegalTaskMutationPersistence mutationPersistence)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        ArgumentNullException.ThrowIfNull(mutationAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);

        _organizationAccessAuthorization = organizationAccessAuthorization;
        _mutationAuthorization = mutationAuthorization;
        _mutationPersistence = mutationPersistence;
    }

    public async Task<ReopenLegalTaskResult> ExecuteAsync(
        ReopenLegalTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        LegalTaskMutationAccess? access =
            await LegalTaskMutationUseCaseSupport.GetAccessAsync(
                _organizationAccessAuthorization,
                command.UserId,
                command.OrganizationId,
                cancellationToken);

        if (access is null)
        {
            return ReopenLegalTaskResult.AccessDenied;
        }

        if (command.TaskId == Guid.Empty)
        {
            return ReopenLegalTaskResult.NotFound;
        }

        var request = new LegalTaskMutationPersistenceRequest(
            access.UserId,
            access.OrganizationId,
            access.MembershipId,
            command.TaskId);

        LegalTaskMutationPersistenceResult persistenceResult =
            await _mutationPersistence.ExecuteAsync(
                request,
                static state => state.LegalTask.CompletedAt is not null
                    ? state.LegalTask.AssigneeMembershipId
                    : null,
                state => Decide(request, state),
                cancellationToken);

        return persistenceResult switch
        {
            LegalTaskMutationPersistenceResult.AccessDenied =>
                ReopenLegalTaskResult.AccessDenied,
            LegalTaskMutationPersistenceResult.NotFound =>
                ReopenLegalTaskResult.NotFound,
            LegalTaskMutationPersistenceResult.RelatedAssigneeUnavailable =>
                ReopenLegalTaskResult.RelatedAssigneeUnavailable,
            LegalTaskMutationPersistenceResult.Succeeded =>
                ReopenLegalTaskResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Legal task reopen returned an invalid result.")
        };
    }

    private LegalTaskMutationDecision Decide(
        LegalTaskMutationPersistenceRequest request,
        LegalTaskMutationLockedState state)
    {
        if (!state.IsOrganizationActive ||
            !LegalTaskMutationUseCaseSupport.IsAvailableActor(
                state.Actor,
                request))
        {
            return LegalTaskMutationDecision.AccessDenied;
        }

        if (!_mutationAuthorization.CanUpdateOrChangeLifecycle(
                state.Actor!.Role,
                state.Actor.MembershipId,
                LegalTaskMutationTaskState.From(state.LegalTask)))
        {
            return LegalTaskMutationDecision.AccessDenied;
        }

        if (state.LegalTask.CompletedAt is not null &&
            state.LegalTask.AssigneeMembershipId is Guid assigneeMembershipId &&
            !IsAvailableAssignee(
                state.Assignee,
                request.OrganizationId,
                assigneeMembershipId))
        {
            return LegalTaskMutationDecision.RelatedAssigneeUnavailable;
        }

        state.LegalTask.Reopen();
        return LegalTaskMutationDecision.Persist;
    }

    private static bool IsAvailableAssignee(
        LegalTaskMutationMemberState? assignee,
        Guid organizationId,
        Guid assigneeMembershipId)
    {
        return assignee is not null &&
            assignee.MembershipId == assigneeMembershipId &&
            assignee.OrganizationId == organizationId &&
            assignee.IsMembershipActive &&
            assignee.IsUserActive;
    }
}
