using Enma.Application.Abstractions;
using Enma.Application.Organizations;
using Enma.Application.Organizations.Create;
using Enma.Application.Security;
using Enma.Application.Users;
using Enma.Domain.Organizations;
using Enma.Domain.Users;

namespace Enma.Application.Onboarding.RegisterOrganizationOwner;

public sealed class RegisterOrganizationOwnerHandler
{
    private readonly IOrganizationRepository organizationRepository;
    private readonly IUserRepository userRepository;
    private readonly IUserCredentialRepository userCredentialRepository;
    private readonly IOrganizationMembershipRepository membershipRepository;
    private readonly IPasswordPolicy passwordPolicy;
    private readonly ICompromisedPasswordChecker compromisedPasswordChecker;
    private readonly IPasswordHasher passwordHasher;
    private readonly IUnitOfWork unitOfWork;
    private readonly TimeProvider timeProvider;

    public RegisterOrganizationOwnerHandler(
        IOrganizationRepository organizationRepository,
        IUserRepository userRepository,
        IUserCredentialRepository userCredentialRepository,
        IOrganizationMembershipRepository membershipRepository,
        IPasswordPolicy passwordPolicy,
        ICompromisedPasswordChecker compromisedPasswordChecker,
        IPasswordHasher passwordHasher,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(organizationRepository);
        ArgumentNullException.ThrowIfNull(userRepository);
        ArgumentNullException.ThrowIfNull(userCredentialRepository);
        ArgumentNullException.ThrowIfNull(membershipRepository);
        ArgumentNullException.ThrowIfNull(passwordPolicy);
        ArgumentNullException.ThrowIfNull(compromisedPasswordChecker);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.organizationRepository = organizationRepository;
        this.userRepository = userRepository;
        this.userCredentialRepository = userCredentialRepository;
        this.membershipRepository = membershipRepository;
        this.passwordPolicy = passwordPolicy;
        this.compromisedPasswordChecker = compromisedPasswordChecker;
        this.passwordHasher = passwordHasher;
        this.unitOfWork = unitOfWork;
        this.timeProvider = timeProvider;
    }

    public async Task<RegisterOrganizationOwnerResult> HandleAsync(
        RegisterOrganizationOwnerCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        DateTimeOffset createdAt = timeProvider.GetUtcNow();
        var organization = new Organization(
            command.OrganizationName,
            command.OrganizationSlug,
            createdAt);
        var user = new User(command.OwnerName, command.OwnerEmail, createdAt);

        passwordPolicy.Validate(command.Password);

        bool slugAlreadyExists = await organizationRepository.ExistsBySlugAsync(
            organization.Slug,
            cancellationToken);

        if (slugAlreadyExists)
        {
            throw new OrganizationSlugAlreadyExistsException(organization.Slug);
        }

        bool emailAlreadyExists = await userRepository.ExistsByEmailAsync(
            user.Email,
            cancellationToken);

        if (emailAlreadyExists)
        {
            throw new UserEmailAlreadyExistsException(user.Email);
        }

        bool isCompromised = await compromisedPasswordChecker.IsCompromisedAsync(
            command.Password,
            cancellationToken);

        if (isCompromised)
        {
            throw new CompromisedPasswordException();
        }

        string passwordHash = passwordHasher.HashPassword(
            user,
            command.Password);
        var credential = new UserCredential(user.Id, passwordHash, createdAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            createdAt);

        await organizationRepository.AddAsync(organization, cancellationToken);
        await userRepository.AddAsync(user, cancellationToken);
        await userCredentialRepository.AddAsync(credential, cancellationToken);
        await membershipRepository.AddAsync(membership, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new RegisterOrganizationOwnerResult(
            organization.Id,
            organization.Name,
            organization.Slug,
            user.Id,
            user.Name,
            user.Email,
            membership.Id,
            membership.Role,
            membership.CreatedAt);
    }
}
