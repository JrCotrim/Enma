using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.Application.Organizations.GetById;

public sealed class GetOrganizationByIdHandler
{
    private readonly OrganizationAccessAuthorization organizationAccessAuthorization;
    private readonly IOrganizationRepository organizationRepository;

    public GetOrganizationByIdHandler(
        OrganizationAccessAuthorization organizationAccessAuthorization,
        IOrganizationRepository organizationRepository)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        ArgumentNullException.ThrowIfNull(organizationRepository);

        this.organizationAccessAuthorization = organizationAccessAuthorization;
        this.organizationRepository = organizationRepository;
    }

    public async Task<GetOrganizationByIdResult> HandleAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        OrganizationAccessAuthorizationResult authorization =
            await organizationAccessAuthorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);

        if (authorization.Status == OrganizationAccessAuthorizationStatus.Denied)
        {
            return GetOrganizationByIdResult.AccessDenied;
        }

        Organization? organization = await organizationRepository.GetByIdAsync(
            organizationId,
            cancellationToken);

        if (organization is null)
        {
            return GetOrganizationByIdResult.NotFound;
        }

        return GetOrganizationByIdResult.Success(
            new OrganizationMetadataReadModel(
                organization.Id,
                organization.Name,
                organization.Slug,
                organization.IsActive,
                organization.CreatedAt));
    }
}
