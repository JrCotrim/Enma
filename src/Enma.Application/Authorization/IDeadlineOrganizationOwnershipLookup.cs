namespace Enma.Application.Authorization;

public interface IDeadlineOrganizationOwnershipLookup
{
    Task<bool> ExistsInOrganizationAsync(
        Guid deadlineId,
        Guid organizationId,
        CancellationToken cancellationToken = default);
}
