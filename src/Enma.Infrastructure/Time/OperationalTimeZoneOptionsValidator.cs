using Microsoft.Extensions.Options;

namespace Enma.Infrastructure.Time;

public sealed class OperationalTimeZoneOptionsValidator
    : IValidateOptions<OperationalTimeZoneOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        OperationalTimeZoneOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string? timeZoneId = options.TimeZoneId;

        if (string.IsNullOrWhiteSpace(timeZoneId) ||
            !string.Equals(timeZoneId, timeZoneId.Trim(), StringComparison.Ordinal) ||
            !TimeZoneInfo.TryFindSystemTimeZoneById(
                timeZoneId,
                out TimeZoneInfo? timeZone) ||
            !timeZone.HasIanaId)
        {
            return ValidateOptionsResult.Fail(
                $"{OperationalTimeZoneOptions.SectionName}:TimeZoneId must be a known IANA time zone ID.");
        }

        return ValidateOptionsResult.Success;
    }
}
