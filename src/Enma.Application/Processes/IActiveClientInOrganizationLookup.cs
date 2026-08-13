namespace Enma.Application.Processes;

public interface IActiveClientInOrganizationLookup
{
    Task<bool> ExistsAsync(
        Guid clientId,
        Guid organizationId,
        CancellationToken cancellationToken = default);
}
