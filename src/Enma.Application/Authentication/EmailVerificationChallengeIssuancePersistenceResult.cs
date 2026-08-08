namespace Enma.Application.Authentication;

public sealed class EmailVerificationChallengeIssuancePersistenceResult
{
    private EmailVerificationChallengeIssuancePersistenceResult(
        bool succeeded,
        string? emailAtIssue)
    {
        Succeeded = succeeded;
        EmailAtIssue = emailAtIssue;
    }

    public static EmailVerificationChallengeIssuancePersistenceResult Rejected { get; } =
        new(false, null);

    public bool Succeeded { get; }

    public string? EmailAtIssue { get; }

    public static EmailVerificationChallengeIssuancePersistenceResult
        CreateSucceeded(string emailAtIssue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emailAtIssue);
        return new EmailVerificationChallengeIssuancePersistenceResult(
            true,
            emailAtIssue);
    }
}
