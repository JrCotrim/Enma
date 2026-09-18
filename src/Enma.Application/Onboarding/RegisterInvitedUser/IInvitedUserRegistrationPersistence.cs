using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;

namespace Enma.Application.Onboarding.RegisterInvitedUser;

public interface IInvitedUserRegistrationPersistence
{
    Task<InvitedUserRegistrationPersistenceResult> RegisterAsync(
        OrganizationInvitationTokenHash invitationTokenHash,
        User user,
        UserCredential credential,
        EmailVerificationChallenge verificationChallenge,
        CancellationToken cancellationToken = default);
}

public enum InvitedUserRegistrationPersistenceResult
{
    InvalidInvitation = 0,
    WrongRecipient = 1,
    ExistingUser = 2,
    Succeeded = 3
}
