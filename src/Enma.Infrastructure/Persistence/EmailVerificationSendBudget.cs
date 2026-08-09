namespace Enma.Infrastructure.Persistence;

public sealed class EmailVerificationSendBudget
{
    private EmailVerificationSendBudget()
    {
    }

    public EmailVerificationSendBudgetScope Scope { get; private set; }

    public byte[] KeyHash { get; private set; } = [];

    public DateTimeOffset WindowStart { get; private set; }

    public int Used { get; private set; }
}
