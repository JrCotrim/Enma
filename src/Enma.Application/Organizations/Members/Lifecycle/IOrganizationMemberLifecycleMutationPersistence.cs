namespace Enma.Application.Organizations.Members.Lifecycle;

public interface IOrganizationMemberLifecycleMutationPersistence
{
    Task<OrganizationMemberLifecycleMutationPersistenceResult> ExecuteAsync(
        OrganizationMemberLifecycleMutationPersistenceRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record OrganizationMemberLifecycleMutationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid TargetMembershipId,
    OrganizationMemberLifecycleOperation Operation);

public enum OrganizationMemberLifecycleOperation
{
    Deactivate = 1,
    Reactivate = 2
}

public enum OrganizationMemberLifecycleMutationPersistenceResult
{
    AccessDenied = 0,
    NotFound = 1,
    ActiveAssignmentsConflict = 2,
    InactiveUserConflict = 3,
    InvalidInput = 4,
    Succeeded = 5
}
