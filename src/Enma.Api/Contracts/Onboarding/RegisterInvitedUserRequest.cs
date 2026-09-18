namespace Enma.Api.Contracts.Onboarding;

public sealed class RegisterInvitedUserRequest
{
    public required string InvitationToken { get; init; }

    public required string Name { get; init; }

    public required string Email { get; init; }

    public required string Password { get; init; }
}
