namespace Enma.Api.Contracts.Authentication;

public sealed class VerifyEmailRequest
{
    public string? Token { get; init; }
}
