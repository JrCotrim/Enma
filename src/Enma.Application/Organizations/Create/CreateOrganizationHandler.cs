using Enma.Application.Abstractions;
using Enma.Domain.Organizations;

namespace Enma.Application.Organizations.Create;

public sealed class CreateOrganizationHandler
{
    private readonly IOrganizationRepository organizationRepository;
    private readonly IUnitOfWork unitOfWork;
    private readonly TimeProvider timeProvider;

    public CreateOrganizationHandler(
        IOrganizationRepository organizationRepository,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(organizationRepository);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.organizationRepository = organizationRepository;
        this.unitOfWork = unitOfWork;
        this.timeProvider = timeProvider;
    }

    public async Task<CreateOrganizationResult> HandleAsync(
        CreateOrganizationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        DateTimeOffset createdAt = timeProvider.GetUtcNow();
        var organization = new Organization(command.Name, command.Slug, createdAt);

        bool slugAlreadyExists = await organizationRepository.ExistsBySlugAsync(
            organization.Slug,
            cancellationToken);

        if (slugAlreadyExists)
        {
            throw new OrganizationSlugAlreadyExistsException(organization.Slug);
        }

        await organizationRepository.AddAsync(organization, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new CreateOrganizationResult(
            organization.Id,
            organization.Name,
            organization.Slug,
            organization.IsActive,
            organization.CreatedAt);
    }
}
