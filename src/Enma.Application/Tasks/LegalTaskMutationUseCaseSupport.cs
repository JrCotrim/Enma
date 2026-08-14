using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.Application.Tasks;

internal static class LegalTaskMutationUseCaseSupport
{
    public static async Task<LegalTaskMutationAccess?> GetAccessAsync(
        OrganizationAccessAuthorization organizationAccessAuthorization,
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        OrganizationAccessAuthorizationResult organizationAccess;

        try
        {
            organizationAccess = await organizationAccessAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);
        }
        catch (ArgumentOutOfRangeException exception) when (
            exception.ParamName == "role")
        {
            return null;
        }

        if (organizationAccess.Status != OrganizationAccessAuthorizationStatus.Allowed ||
            organizationAccess.UserId != userId ||
            organizationAccess.OrganizationId != organizationId ||
            organizationAccess.MembershipId is not Guid membershipId ||
            organizationAccess.Role is not OrganizationRole)
        {
            return null;
        }

        return new LegalTaskMutationAccess(userId, organizationId, membershipId);
    }

    public static bool IsAvailableActor(
        LegalTaskMutationMemberState? actor,
        LegalTaskMutationPersistenceRequest request)
    {
        return actor is not null &&
            actor.MembershipId == request.ActorMembershipId &&
            actor.OrganizationId == request.OrganizationId &&
            actor.UserId == request.UserId &&
            actor.IsMembershipActive &&
            actor.IsUserActive &&
            Enum.IsDefined(actor.Role);
    }
}

internal sealed record LegalTaskMutationAccess(
    Guid UserId,
    Guid OrganizationId,
    Guid MembershipId);
