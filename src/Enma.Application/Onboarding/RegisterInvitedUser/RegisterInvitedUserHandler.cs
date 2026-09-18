using Enma.Application.Authentication;
using Enma.Application.Organizations.Invitations;
using Enma.Application.Security;
using Enma.Application.Validation;
using Enma.Domain.Authentication;
using Enma.Domain.Users;

namespace Enma.Application.Onboarding.RegisterInvitedUser;

public sealed class RegisterInvitedUserHandler
{
    private readonly IOrganizationInvitationTokenService invitationTokenService;
    private readonly IInvitedUserRegistrationPersistence persistence;
    private readonly IPasswordPolicy passwordPolicy;
    private readonly ICompromisedPasswordChecker compromisedPasswordChecker;
    private readonly IPasswordHasher passwordHasher;
    private readonly IEmailVerificationTokenService verificationTokenService;
    private readonly IEmailVerificationDelivery verificationDelivery;
    private readonly TimeProvider timeProvider;

    public RegisterInvitedUserHandler(
        IOrganizationInvitationTokenService invitationTokenService,
        IInvitedUserRegistrationPersistence persistence,
        IPasswordPolicy passwordPolicy,
        ICompromisedPasswordChecker compromisedPasswordChecker,
        IPasswordHasher passwordHasher,
        IEmailVerificationTokenService verificationTokenService,
        IEmailVerificationDelivery verificationDelivery,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(invitationTokenService);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(passwordPolicy);
        ArgumentNullException.ThrowIfNull(compromisedPasswordChecker);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(verificationTokenService);
        ArgumentNullException.ThrowIfNull(verificationDelivery);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.invitationTokenService = invitationTokenService;
        this.persistence = persistence;
        this.passwordPolicy = passwordPolicy;
        this.compromisedPasswordChecker = compromisedPasswordChecker;
        this.passwordHasher = passwordHasher;
        this.verificationTokenService = verificationTokenService;
        this.verificationDelivery = verificationDelivery;
        this.timeProvider = timeProvider;
    }

    public async Task<RegisterInvitedUserResult> HandleAsync(
        RegisterInvitedUserCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!invitationTokenService.TryHashToken(
                command.InvitationToken,
                out var invitationTokenHash) ||
            invitationTokenHash is null)
        {
            return new RegisterInvitedUserResult(
                RegisterInvitedUserStatus.InvalidInvitation);
        }

        DateTimeOffset createdAt = timeProvider.GetUtcNow();
        User user = CreateUser(command.Name, command.Email, createdAt);
        ValidatePassword(command.Password);

        if (await compromisedPasswordChecker.IsCompromisedAsync(
                command.Password,
                cancellationToken))
        {
            throw new CompromisedPasswordException();
        }

        var credential = new UserCredential(
            user.Id,
            passwordHasher.HashPassword(command.Password),
            createdAt);
        string rawVerificationToken = verificationTokenService.GenerateToken(
            out EmailVerificationTokenHash verificationTokenHash);
        var verificationChallenge = new EmailVerificationChallenge(
            user.Id,
            user.Email,
            verificationTokenHash,
            createdAt,
            createdAt.Add(EmailVerificationPolicy.TokenLifetime));

        InvitedUserRegistrationPersistenceResult persistenceResult =
            await persistence.RegisterAsync(
                invitationTokenHash,
                user,
                credential,
                verificationChallenge,
                cancellationToken);

        if (persistenceResult != InvitedUserRegistrationPersistenceResult.Succeeded)
        {
            return new RegisterInvitedUserResult(
                persistenceResult switch
                {
                    InvitedUserRegistrationPersistenceResult.WrongRecipient =>
                        RegisterInvitedUserStatus.WrongRecipient,
                    InvitedUserRegistrationPersistenceResult.ExistingUser =>
                        RegisterInvitedUserStatus.ExistingUser,
                    _ => RegisterInvitedUserStatus.InvalidInvitation
                });
        }

        EmailVerificationDeliveryResult deliveryResult =
            await verificationDelivery.DeliverAsync(
                user.Email,
                rawVerificationToken,
                command.InvitationToken!,
                cancellationToken);

        return new RegisterInvitedUserResult(
            RegisterInvitedUserStatus.Succeeded,
            deliveryResult == EmailVerificationDeliveryResult.Delivered);
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
