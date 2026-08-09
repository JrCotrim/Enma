namespace Enma.Infrastructure.Email;

public sealed class EmailVerificationSendBudgetOptions
{
    public const string SectionName = "EmailVerification:SendBudget";

    public int GlobalHourlyLimit { get; init; } = 100;

    public int DestinationDailyLimit { get; init; } = 5;
}
