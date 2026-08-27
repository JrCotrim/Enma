namespace Enma.Application.Organizations.UpdateName;

public interface IOrganizationNameMutationPersistence
{
    Task<OrganizationNameMutationPersistenceResult> ExecuteAsync(
        OrganizationNameMutationPersistenceRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record OrganizationNameMutationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    string Name);

public enum OrganizationNameMutationPersistenceResult
{
    AccessDenied = 0,
    InvalidInput = 1,
    Succeeded = 2
}
