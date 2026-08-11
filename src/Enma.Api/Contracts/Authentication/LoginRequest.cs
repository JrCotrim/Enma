namespace Enma.Api.Contracts.Authentication;

public sealed class LoginRequest
{
    public string? Email { get; init; }

    public string? Password { get; init; }
}
