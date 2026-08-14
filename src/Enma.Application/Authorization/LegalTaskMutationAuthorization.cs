using Enma.Application.Tasks;
using Enma.Domain.Organizations;

namespace Enma.Application.Authorization;

public sealed class LegalTaskMutationAuthorization
{
    public bool CanUpdateOrChangeLifecycle(
        OrganizationRole role,
        Guid actorMembershipId,
        LegalTaskMutationTaskState legalTask)
    {
        ArgumentNullException.ThrowIfNull(legalTask);

        return role switch
        {
            OrganizationRole.Owner or OrganizationRole.Administrator => true,
            OrganizationRole.Member => IsOwnedBy(actorMembershipId, legalTask),
            _ => false
        };
    }

    public bool CanChangeAssignee(
        OrganizationRole role,
        Guid actorMembershipId,
        Guid? currentAssigneeMembershipId,
        Guid? requestedAssigneeMembershipId)
    {
        return role switch
        {
            OrganizationRole.Owner or OrganizationRole.Administrator => true,
            OrganizationRole.Member when currentAssigneeMembershipId is null =>
                requestedAssigneeMembershipId is null ||
                requestedAssigneeMembershipId == actorMembershipId,
            OrganizationRole.Member
                when currentAssigneeMembershipId == actorMembershipId =>
                requestedAssigneeMembershipId is null ||
                requestedAssigneeMembershipId == actorMembershipId,
            _ => false
        };
    }

    private static bool IsOwnedBy(
        Guid actorMembershipId,
        LegalTaskMutationTaskState legalTask)
    {
        return legalTask.AssigneeMembershipId == actorMembershipId ||
            legalTask.AssigneeMembershipId is null &&
            legalTask.CreatedByMembershipId == actorMembershipId;
    }
}
