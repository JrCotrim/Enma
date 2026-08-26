using Enma.Application.Authorization;

namespace Enma.Application.Organizations.Members.Lifecycle;

public sealed class OrganizationMemberLifecycleUseCase
{
    private readonly OrganizationAdministrationAuthorization _authorization;
    private readonly IOrganizationMemberLifecycleMutationPersistence _persistence;

    public OrganizationMemberLifecycleUseCase(
        OrganizationAdministrationAuthorization authorization,
        IOrganizationMemberLifecycleMutationPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(persistence);

        _authorization = authorization;
        _persistence = persistence;
    }

    public Task<OrganizationMemberLifecycleResult> DeactivateAsync(
        Guid userId,
        Guid organizationId,
        Guid membershipId,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            userId,
            organizationId,
            membershipId,
            OrganizationMemberLifecycleOperation.Deactivate,
            OrganizationAdministrationAction.DeactivateMember,
            cancellationToken);
    }

    public Task<OrganizationMemberLifecycleResult> ReactivateAsync(
        Guid userId,
        Guid organizationId,
        Guid membershipId,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            userId,
            organizationId,
            membershipId,
            OrganizationMemberLifecycleOperation.Reactivate,
            OrganizationAdministrationAction.ReactivateMember,
            cancellationToken);
    }

    private async Task<OrganizationMemberLifecycleResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        Guid membershipId,
        OrganizationMemberLifecycleOperation operation,
        OrganizationAdministrationAction action,
        CancellationToken cancellationToken)
    {
        OrganizationAdministrationAuthorizationResult authorization =
            await _authorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);

        if (!authorization.Allows(action) ||
            authorization.UserId != userId ||
            authorization.OrganizationId != organizationId ||
            authorization.MembershipId is not Guid actorMembershipId ||
            actorMembershipId == Guid.Empty)
        {
            return OrganizationMemberLifecycleResult.AccessDenied;
        }

        if (membershipId == Guid.Empty)
        {
            return OrganizationMemberLifecycleResult.NotFound;
        }

        var request = new OrganizationMemberLifecycleMutationPersistenceRequest(
            userId,
            organizationId,
            actorMembershipId,
            membershipId,
            operation);
        OrganizationMemberLifecycleMutationPersistenceResult persistenceResult =
            await _persistence.ExecuteAsync(request, cancellationToken);

        return persistenceResult switch
        {
            OrganizationMemberLifecycleMutationPersistenceResult.AccessDenied =>
                OrganizationMemberLifecycleResult.AccessDenied,
            OrganizationMemberLifecycleMutationPersistenceResult.NotFound =>
                OrganizationMemberLifecycleResult.NotFound,
            OrganizationMemberLifecycleMutationPersistenceResult
                .ActiveAssignmentsConflict =>
                OrganizationMemberLifecycleResult.ActiveAssignmentsConflict,
            OrganizationMemberLifecycleMutationPersistenceResult
                .InactiveUserConflict =>
                OrganizationMemberLifecycleResult.InactiveUserConflict,
            OrganizationMemberLifecycleMutationPersistenceResult.Succeeded =>
                OrganizationMemberLifecycleResult.Succeeded,
            OrganizationMemberLifecycleMutationPersistenceResult.InvalidInput =>
                throw new InvalidOperationException(
                    "Validated organization member lifecycle input was rejected by persistence."),
            _ => throw new InvalidOperationException(
                "Organization member lifecycle persistence returned an invalid result.")
        };
    }
}

public enum OrganizationMemberLifecycleResult
{
    AccessDenied = 0,
    NotFound = 1,
    ActiveAssignmentsConflict = 2,
    InactiveUserConflict = 3,
    Succeeded = 4
}
