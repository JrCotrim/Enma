using Enma.Domain.Organizations;

namespace Enma.Application.Organizations.Members.Role;

public interface IOrganizationMemberRoleMutationPersistence
{
    Task<OrganizationMemberRoleMutationPersistenceResult> ExecuteAsync(
        OrganizationMemberRoleMutationPersistenceRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record OrganizationMemberRoleMutationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid TargetMembershipId,
    OrganizationRole Role,
    OrganizationRole ExpectedCurrentRole);

public enum OrganizationMemberRoleMutationPersistenceResult
{
    AccessDenied = 0,
    NotFound = 1,
    TargetForbidden = 2,
    InvalidInput = 3,
    Conflict = 4,
    Succeeded = 5
}
