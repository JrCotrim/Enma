namespace Enma.Api.Contracts.Authentication;

public sealed class RequestPasswordRecoveryRequest
{
    public string? Email { get; init; }
}

public sealed class ResetPasswordRequest
{
    public string? Token { get; init; }

    public string? NewPassword { get; init; }
}
