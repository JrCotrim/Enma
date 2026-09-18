using Enma.Application.Authentication;
using Enma.Application.Onboarding.RegisterInvitedUser;
using Enma.Application.Organizations.Invitations;
using Enma.Application.Security;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;

namespace Enma.UnitTests.Application.Onboarding;

public sealed class RegisterInvitedUserHandlerTests
{
    private const string InvitationToken =
        "Abcdefghijklmnopqrstuvwxyz0123456789_-ABCDE";
    private static readonly DateTimeOffset Now =
        new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_ValidInvitation_CreatesOnlyIdentityAndChallenge()
    {
        var persistence = new RecordingPersistence();
        var delivery = new RecordingDelivery();
        RegisterInvitedUserHandler handler = CreateHandler(persistence, delivery);

        RegisterInvitedUserResult result = await handler.HandleAsync(
            new RegisterInvitedUserCommand(
                InvitationToken,
                "  Pessoa Convidada  ",
                "  PERSON@example.com ",
                "Synthetic!Password42"));

        Assert.Equal(RegisterInvitedUserStatus.Succeeded, result.Status);
        Assert.True(result.VerificationEmailSent);
        Assert.NotNull(persistence.User);
        Assert.Equal("Pessoa Convidada", persistence.User.Name);
        Assert.Equal("person@example.com", persistence.User.Email);
        Assert.Null(persistence.User.EmailVerifiedAt);
        Assert.Equal(persistence.User.Id, persistence.Credential?.UserId);
        Assert.Equal(persistence.User.Id, persistence.Challenge?.UserId);
        Assert.Equal("person@example.com", persistence.Challenge?.EmailAtIssue);
        Assert.Equal("person@example.com", delivery.Email);
        Assert.Equal("verification-token", delivery.RawToken);
        Assert.Equal(InvitationToken, delivery.InvitationToken);
    }

    [Fact]
    public async Task HandleAsync_InvalidToken_FailsBeforeIdentityWork()
    {
        var persistence = new RecordingPersistence();
        var delivery = new RecordingDelivery();
        RegisterInvitedUserHandler handler = CreateHandler(persistence, delivery);

        RegisterInvitedUserResult result = await handler.HandleAsync(
            new RegisterInvitedUserCommand(
                "invalid",
                "Pessoa Convidada",
                "person@example.com",
                "Synthetic!Password42"));

        Assert.Equal(RegisterInvitedUserStatus.InvalidInvitation, result.Status);
        Assert.Null(persistence.User);
        Assert.Null(delivery.Email);
    }

    [Theory]
    [InlineData(
        InvitedUserRegistrationPersistenceResult.InvalidInvitation,
        RegisterInvitedUserStatus.InvalidInvitation)]
    [InlineData(
        InvitedUserRegistrationPersistenceResult.WrongRecipient,
        RegisterInvitedUserStatus.WrongRecipient)]
    [InlineData(
        InvitedUserRegistrationPersistenceResult.ExistingUser,
        RegisterInvitedUserStatus.ExistingUser)]
    public async Task HandleAsync_RejectedPersistence_DoesNotDeliver(
        InvitedUserRegistrationPersistenceResult persistenceResult,
        RegisterInvitedUserStatus expectedStatus)
    {
        var persistence = new RecordingPersistence
        {
            Result = persistenceResult
        };
        var delivery = new RecordingDelivery();
        RegisterInvitedUserHandler handler = CreateHandler(persistence, delivery);

        RegisterInvitedUserResult result = await handler.HandleAsync(
            new RegisterInvitedUserCommand(
                InvitationToken,
                "Pessoa Convidada",
                "person@example.com",
                "Synthetic!Password42"));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(delivery.Email);
    }

    [Fact]
    public async Task HandleAsync_DeliveryFailure_KeepsRecoverableAccount()
    {
        var persistence = new RecordingPersistence();
        var delivery = new RecordingDelivery
        {
            Result = EmailVerificationDeliveryResult.Failed
        };
        RegisterInvitedUserHandler handler = CreateHandler(persistence, delivery);

        RegisterInvitedUserResult result = await handler.HandleAsync(
            new RegisterInvitedUserCommand(
                InvitationToken,
                "Pessoa Convidada",
                "person@example.com",
                "Synthetic!Password42"));

        Assert.Equal(RegisterInvitedUserStatus.Succeeded, result.Status);
        Assert.False(result.VerificationEmailSent);
        Assert.NotNull(persistence.User);
        Assert.Equal("person@example.com", delivery.Email);
    }

    private static RegisterInvitedUserHandler CreateHandler(
        RecordingPersistence persistence,
        RecordingDelivery delivery)
    {
        return new RegisterInvitedUserHandler(
            new FixedInvitationTokenService(),
            persistence,
            new DefaultPasswordPolicy(),
            new SafePasswordChecker(),
            new FixedPasswordHasher(),
            new FixedVerificationTokenService(),
            delivery,
            new FixedTimeProvider(Now));
    }

    private sealed class RecordingPersistence
        : IInvitedUserRegistrationPersistence
    {
        public InvitedUserRegistrationPersistenceResult Result { get; init; } =
            InvitedUserRegistrationPersistenceResult.Succeeded;

        public User? User { get; private set; }

        public UserCredential? Credential { get; private set; }

        public EmailVerificationChallenge? Challenge { get; private set; }

        public Task<InvitedUserRegistrationPersistenceResult> RegisterAsync(
            OrganizationInvitationTokenHash invitationTokenHash,
            User user,
            UserCredential credential,
            EmailVerificationChallenge verificationChallenge,
            CancellationToken cancellationToken = default)
        {
            User = user;
            Credential = credential;
            Challenge = verificationChallenge;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingDelivery : IEmailVerificationDelivery
    {
        public EmailVerificationDeliveryResult Result { get; init; } =
            EmailVerificationDeliveryResult.Delivered;

        public string? Email { get; private set; }

        public string? RawToken { get; private set; }

        public string? InvitationToken { get; private set; }

        public Task<EmailVerificationDeliveryResult> DeliverAsync(
            string email,
            string rawToken,
            CancellationToken cancellationToken = default)
        {
            Email = email;
            RawToken = rawToken;
            return Task.FromResult(Result);
        }

        public Task<EmailVerificationDeliveryResult> DeliverAsync(
            string email,
            string rawToken,
            string invitationToken,
            CancellationToken cancellationToken = default)
        {
            InvitationToken = invitationToken;
            return DeliverAsync(email, rawToken, cancellationToken);
        }
    }

    private sealed class FixedInvitationTokenService
        : IOrganizationInvitationTokenService
    {
        public string GenerateToken(out OrganizationInvitationTokenHash tokenHash)
        {
            tokenHash = CreateHash();
            return InvitationToken;
        }

        public bool TryHashToken(
            string? rawToken,
            out OrganizationInvitationTokenHash? tokenHash)
        {
            tokenHash = rawToken == InvitationToken ? CreateHash() : null;
            return tokenHash is not null;
        }

        private static OrganizationInvitationTokenHash CreateHash()
        {
            return new OrganizationInvitationTokenHash(
                Enumerable.Repeat((byte)1, 32).ToArray());
        }
    }

    private sealed class FixedVerificationTokenService
        : IEmailVerificationTokenService
    {
        public string GenerateToken(out EmailVerificationTokenHash tokenHash)
        {
            tokenHash = new EmailVerificationTokenHash(
                Enumerable.Repeat((byte)2, 32).ToArray());
            return "verification-token";
        }

        public bool TryHashToken(
            string? rawToken,
            out EmailVerificationTokenHash? tokenHash)
        {
            tokenHash = null;
            return false;
        }
    }

    private sealed class SafePasswordChecker : ICompromisedPasswordChecker
    {
        public Task<bool> IsCompromisedAsync(
            string password,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }
    }

    private sealed class FixedPasswordHasher : IPasswordHasher
    {
        public string HashPassword(string password) => "synthetic-password-hash";

        public PasswordVerificationResult VerifyHashedPassword(
            string passwordHash,
            string providedPassword)
        {
            return PasswordVerificationResult.Success;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
