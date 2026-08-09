using Microsoft.Extensions.Options;

namespace Enma.Infrastructure.Email;

public sealed class EmailVerificationSendBudgetOptionsValidator
    : IValidateOptions<EmailVerificationSendBudgetOptions>
{
    public const int MaximumGlobalHourlyLimit = 1_000_000;
    public const int MaximumDestinationDailyLimit = 10_000;

    public ValidateOptionsResult Validate(
        string? name,
        EmailVerificationSendBudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.GlobalHourlyLimit is < 1 or > MaximumGlobalHourlyLimit)
        {
            failures.Add(
                $"{EmailVerificationSendBudgetOptions.SectionName}:GlobalHourlyLimit must be between 1 and {MaximumGlobalHourlyLimit}.");
        }

        if (options.DestinationDailyLimit is < 1
            or > MaximumDestinationDailyLimit)
        {
            failures.Add(
                $"{EmailVerificationSendBudgetOptions.SectionName}:DestinationDailyLimit must be between 1 and {MaximumDestinationDailyLimit}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
