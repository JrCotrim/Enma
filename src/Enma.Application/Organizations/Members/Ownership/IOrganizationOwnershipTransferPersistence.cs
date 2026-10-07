using Enma.Domain.Organizations;

namespace Enma.Application.Organizations.Members.Ownership;

/// <summary>
/// Transfers ownership atomically. Implementations must revalidate the live actor,
/// the target, and the expected target role on locked rows inside one transaction.
/// </summary>
public interface IOrganizationOwnershipTransferPersistence
{
    Task<OrganizationOwnershipTransferPersistenceResult> ExecuteAsync(
        OrganizationOwnershipTransferPersistenceRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record OrganizationOwnershipTransferPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid TargetMembershipId,
    OrganizationRole ExpectedTargetRole);

public enum OrganizationOwnershipTransferPersistenceResult
{
    AccessDenied = 0,
    NotFound = 1,
    TargetUnavailable = 2,
    InvalidInput = 3,
    Succeeded = 4
}
