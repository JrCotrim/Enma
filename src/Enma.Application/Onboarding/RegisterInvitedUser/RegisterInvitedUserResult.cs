namespace Enma.Application.Onboarding.RegisterInvitedUser;

public sealed record RegisterInvitedUserResult(
    RegisterInvitedUserStatus Status,
    bool VerificationEmailSent = false);

public enum RegisterInvitedUserStatus
{
    InvalidInvitation = 0,
    WrongRecipient = 1,
    ExistingUser = 2,
    Succeeded = 3
}
