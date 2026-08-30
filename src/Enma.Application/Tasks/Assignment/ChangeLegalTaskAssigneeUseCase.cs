using Enma.Application.Authorization;
using Enma.Domain.Tasks;

namespace Enma.Application.Tasks.Assignment;

public sealed class ChangeLegalTaskAssigneeUseCase
{
    private readonly OrganizationAccessAuthorization _organizationAccessAuthorization;
    private readonly LegalTaskMutationAuthorization _mutationAuthorization;
    private readonly ILegalTaskMutationPersistence _mutationPersistence;

    public ChangeLegalTaskAssigneeUseCase(
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

    public async Task<ChangeLegalTaskAssigneeResult> ExecuteAsync(
        ChangeLegalTaskAssigneeCommand command,
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
            return ChangeLegalTaskAssigneeResult.AccessDenied;
        }

        if (command.TaskId == Guid.Empty)
        {
            return ChangeLegalTaskAssigneeResult.NotFound;
        }

        var request = new LegalTaskMutationPersistenceRequest(
            access.UserId,
            access.OrganizationId,
            access.MembershipId,
            command.TaskId);

        LegalTaskMutationPersistenceResult persistenceResult =
            await _mutationPersistence.ExecuteAsync(
                request,
                state => SelectAssigneeToLock(command, request, state),
                state => Decide(command, request, state),
                cancellationToken);

        return persistenceResult switch
        {
            LegalTaskMutationPersistenceResult.AccessDenied =>
                ChangeLegalTaskAssigneeResult.AccessDenied,
            LegalTaskMutationPersistenceResult.NotFound =>
                ChangeLegalTaskAssigneeResult.NotFound,
            LegalTaskMutationPersistenceResult.RelatedAssigneeUnavailable =>
                ChangeLegalTaskAssigneeResult.RelatedAssigneeUnavailable,
            LegalTaskMutationPersistenceResult.InvalidInput =>
                ChangeLegalTaskAssigneeResult.InvalidInput,
            LegalTaskMutationPersistenceResult.Conflict =>
                ChangeLegalTaskAssigneeResult.Conflict,
            LegalTaskMutationPersistenceResult.Succeeded =>
                ChangeLegalTaskAssigneeResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Legal task mutation persistence returned an invalid result.")
        };
    }

    private Guid? SelectAssigneeToLock(
        ChangeLegalTaskAssigneeCommand command,
        LegalTaskMutationPersistenceRequest request,
        LegalTaskMutationPreviewState state)
    {
        if (!LegalTaskMutationUseCaseSupport.IsAvailableActor(
                state.Actor,
                request) ||
            command.AssigneeMembershipId is not Guid requestedAssigneeId ||
            requestedAssigneeId == Guid.Empty ||
            requestedAssigneeId == state.LegalTask.AssigneeMembershipId ||
            state.LegalTask.CompletedAt is not null)
        {
            return null;
        }

        return _mutationAuthorization.CanChangeAssignee(
            state.Actor!.Role,
            state.Actor.MembershipId,
            state.LegalTask.AssigneeMembershipId,
            command.AssigneeMembershipId)
                ? requestedAssigneeId
                : null;
    }

    private LegalTaskMutationDecision Decide(
        ChangeLegalTaskAssigneeCommand command,
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

        if (!_mutationAuthorization.CanChangeAssignee(
                state.Actor!.Role,
                state.Actor.MembershipId,
                state.LegalTask.AssigneeMembershipId,
                command.AssigneeMembershipId))
        {
            return LegalTaskMutationDecision.AccessDenied;
        }

        if (state.LegalTask.CompletedAt is not null)
        {
            return LegalTaskMutationDecision.Conflict;
        }

        if (command.AssigneeMembershipId == Guid.Empty)
        {
            return LegalTaskMutationDecision.InvalidInput;
        }

        if (command.AssigneeMembershipId is Guid requestedAssigneeId &&
            requestedAssigneeId != state.LegalTask.AssigneeMembershipId)
        {
            if (!state.AssigneeLookupPerformed)
            {
                return LegalTaskMutationDecision.LockAssignee(
                    requestedAssigneeId);
            }

            if (!IsAvailableAssignee(
                    state.Assignee,
                    request.OrganizationId,
                    requestedAssigneeId))
            {
                return LegalTaskMutationDecision.RelatedAssigneeUnavailable;
            }
        }

        try
        {
            state.LegalTask.ChangeAssignee(command.AssigneeMembershipId);
            return LegalTaskMutationDecision.Persist;
        }
        catch (LegalTaskCompletedMutationException)
        {
            return LegalTaskMutationDecision.Conflict;
        }
        catch (ArgumentException exception) when (
            exception.ParamName == "assigneeMembershipId")
        {
            return LegalTaskMutationDecision.InvalidInput;
        }
    }

    private static bool IsAvailableAssignee(
        LegalTaskMutationMemberState? assignee,
        Guid organizationId,
        Guid requestedAssigneeId)
    {
        return assignee is not null &&
            assignee.MembershipId == requestedAssigneeId &&
            assignee.OrganizationId == organizationId &&
            assignee.IsMembershipActive &&
            assignee.IsUserActive;
    }
}
