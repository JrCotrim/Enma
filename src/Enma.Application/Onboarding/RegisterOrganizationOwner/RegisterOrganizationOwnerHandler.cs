using Enma.Application.Abstractions;
using Enma.Application.Authentication;
using Enma.Application.Organizations;
using Enma.Application.Organizations.Create;
using Enma.Application.Security;
using Enma.Application.Users;
using Enma.Application.Validation;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;

namespace Enma.Application.Onboarding.RegisterOrganizationOwner;

public sealed class RegisterOrganizationOwnerHandler
{
    private readonly IOrganizationRepository organizationRepository;
    private readonly IUserRepository userRepository;
    private readonly IUserCredentialRepository userCredentialRepository;
    private readonly IOrganizationMembershipRepository membershipRepository;
    private readonly IEmailVerificationChallengeRepository
        emailVerificationChallengeRepository;
    private readonly IPasswordPolicy passwordPolicy;
    private readonly ICompromisedPasswordChecker compromisedPasswordChecker;
    private readonly IPasswordHasher passwordHasher;
    private readonly IEmailVerificationTokenService emailVerificationTokenService;
    private readonly IEmailVerificationDelivery emailVerificationDelivery;
    private readonly IUnitOfWork unitOfWork;
    private readonly TimeProvider timeProvider;

    public RegisterOrganizationOwnerHandler(
        IOrganizationRepository organizationRepository,
        IUserRepository userRepository,
        IUserCredentialRepository userCredentialRepository,
        IOrganizationMembershipRepository membershipRepository,
        IEmailVerificationChallengeRepository emailVerificationChallengeRepository,
        IPasswordPolicy passwordPolicy,
        ICompromisedPasswordChecker compromisedPasswordChecker,
        IPasswordHasher passwordHasher,
        IEmailVerificationTokenService emailVerificationTokenService,
        IEmailVerificationDelivery emailVerificationDelivery,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(organizationRepository);
        ArgumentNullException.ThrowIfNull(userRepository);
        ArgumentNullException.ThrowIfNull(userCredentialRepository);
        ArgumentNullException.ThrowIfNull(membershipRepository);
        ArgumentNullException.ThrowIfNull(emailVerificationChallengeRepository);
        ArgumentNullException.ThrowIfNull(passwordPolicy);
        ArgumentNullException.ThrowIfNull(compromisedPasswordChecker);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(emailVerificationTokenService);
        ArgumentNullException.ThrowIfNull(emailVerificationDelivery);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.organizationRepository = organizationRepository;
        this.userRepository = userRepository;
        this.userCredentialRepository = userCredentialRepository;
        this.membershipRepository = membershipRepository;
        this.emailVerificationChallengeRepository =
            emailVerificationChallengeRepository;
        this.passwordPolicy = passwordPolicy;
        this.compromisedPasswordChecker = compromisedPasswordChecker;
        this.passwordHasher = passwordHasher;
        this.emailVerificationTokenService = emailVerificationTokenService;
        this.emailVerificationDelivery = emailVerificationDelivery;
        this.unitOfWork = unitOfWork;
        this.timeProvider = timeProvider;
    }

    public async Task<RegisterOrganizationOwnerResult> HandleAsync(
        RegisterOrganizationOwnerCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        DateTimeOffset createdAt = timeProvider.GetUtcNow();
        Organization organization = CreateOrganization(
            command.OrganizationName,
            command.OrganizationSlug,
            createdAt);
        User user = CreateUser(command.OwnerName, command.OwnerEmail, createdAt);

        ValidatePassword(command.Password);

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

        string passwordHash = passwordHasher.HashPassword(command.Password);
        var credential = new UserCredential(user.Id, passwordHash, createdAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            createdAt);
        string rawToken = emailVerificationTokenService.GenerateToken(
            out EmailVerificationTokenHash tokenHash);
        var emailVerificationChallenge = new EmailVerificationChallenge(
            user.Id,
            user.Email,
            tokenHash,
            createdAt,
            createdAt.Add(EmailVerificationPolicy.TokenLifetime));

        await organizationRepository.AddAsync(organization, cancellationToken);
        await userRepository.AddAsync(user, cancellationToken);
        await userCredentialRepository.AddAsync(credential, cancellationToken);
        await membershipRepository.AddAsync(membership, cancellationToken);
        await emailVerificationChallengeRepository.AddAsync(
            emailVerificationChallenge,
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var result = new RegisterOrganizationOwnerResult(
            organization.Id,
            organization.Name,
            organization.Slug,
            user.Id,
            user.Name,
            user.Email,
            membership.Id,
            membership.Role,
            membership.CreatedAt);

        _ = await emailVerificationDelivery.DeliverAsync(
            result.UserEmail,
            rawToken,
            cancellationToken);

        return result;
    }

    private static Organization CreateOrganization(
        string name,
        string slug,
        DateTimeOffset createdAt)
    {
        try
        {
            return new Organization(name, slug, createdAt);
        }
        catch (ArgumentException exception)
            when (exception.ParamName is "name" or "slug")
        {
            throw new RequestValidationException(exception.Message, exception);
        }
    }

    private static User CreateUser(
        string name,
        string email,
        DateTimeOffset createdAt)
    {
        try
        {
            return new User(name, email, createdAt);
        }
        catch (ArgumentException exception)
            when (exception.ParamName is "name" or "email")
        {
            throw new RequestValidationException(exception.Message, exception);
        }
    }

    private void ValidatePassword(string password)
    {
        try
        {
            passwordPolicy.Validate(password);
        }
        catch (ArgumentException exception)
            when (exception.ParamName == "password")
        {
            throw new RequestValidationException(exception.Message, exception);
        }
    }
}
