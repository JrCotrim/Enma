namespace Enma.Application.Onboarding.RegisterInvitedUser;

public sealed record RegisterInvitedUserCommand(
    string? InvitationToken,
    string Name,
    string Email,
    string Password);
