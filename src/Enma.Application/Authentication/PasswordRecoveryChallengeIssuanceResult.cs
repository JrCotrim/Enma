namespace Enma.Application.Authentication;

public sealed class PasswordRecoveryChallengeIssuanceResult
{
    private PasswordRecoveryChallengeIssuanceResult(bool succeeded, string? email)
    {
        Succeeded = succeeded;
        Email = email;
    }

    public static PasswordRecoveryChallengeIssuanceResult Rejected { get; } =
        new(false, null);

    public bool Succeeded { get; }

    public string? Email { get; }

    public static PasswordRecoveryChallengeIssuanceResult CreateSucceeded(
        string email) => new(true, email);
}
