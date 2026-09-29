using Enma.Application.Authorization;

namespace Enma.Application.Processes;

internal static class LegalProcessOperationalMutationSupport
{
    public static async Task<Guid?> AuthorizeActorMembershipAsync(
        ProcessActionAuthorization actionAuthorization,
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        OrganizationAccessAuthorizationResult authorization =
            await actionAuthorization.AuthorizeActorAsync(
                userId,
                organizationId,
                ProcessAction.Update,
                cancellationToken);

        return authorization.MembershipId;
    }

    public static bool IsActorAllowed(
        ProcessActionAuthorization actionAuthorization,
        LegalProcessOperationalMutationPersistenceRequest request,
        LegalProcessOperationalMutationLockedState state)
    {
        return state.IsOrganizationActive &&
            state.Actor is { } actor &&
            actor.IsAvailableFor(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) &&
            actionAuthorization.CanExecute(ProcessAction.Update, actor.Role);
    }

    public static bool IsRelatedMemberAvailable(
        LegalProcessOperationalMutationLockedState state,
        Guid organizationId,
        Guid membershipId)
    {
        return state.RelatedMember?.IsAvailableMemberOf(
            organizationId,
            membershipId) == true;
    }
}
