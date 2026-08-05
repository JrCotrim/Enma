using Enma.Domain.Organizations;

namespace Enma.Application.Organizations.GetById;

public sealed class GetOrganizationByIdHandler
{
    private readonly IOrganizationRepository organizationRepository;

    public GetOrganizationByIdHandler(
        IOrganizationRepository organizationRepository)
    {
        ArgumentNullException.ThrowIfNull(organizationRepository);
        this.organizationRepository = organizationRepository;
    }

    public async Task<GetOrganizationByIdResult> HandleAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Organization id cannot be empty.",
                nameof(organizationId));
        }

        Organization? organization = await organizationRepository.GetByIdAsync(
            organizationId,
            cancellationToken);

        if (organization is null)
        {
            throw new OrganizationNotFoundException(organizationId);
        }

        return new GetOrganizationByIdResult(
            organization.Id,
            organization.Name,
            organization.Slug,
            organization.IsActive,
            organization.CreatedAt);
    }
}
