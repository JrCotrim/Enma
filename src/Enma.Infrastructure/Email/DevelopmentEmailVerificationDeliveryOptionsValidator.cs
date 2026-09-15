using Microsoft.Extensions.Options;

namespace Enma.Infrastructure.Email;

public sealed class DevelopmentEmailVerificationDeliveryOptionsValidator
    : IValidateOptions<DevelopmentEmailVerificationDeliveryOptions>
{
    private const int MaximumVerificationPageUrlLength = 2_048;

    public ValidateOptionsResult Validate(
        string? name,
        DevelopmentEmailVerificationDeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        ValidatePageUrl(
            options.VerificationPageUrl,
            nameof(options.VerificationPageUrl),
            "/verify-email",
            failures);
        ValidatePageUrl(
            options.PasswordRecoveryPageUrl,
            nameof(options.PasswordRecoveryPageUrl),
            "/reset-password",
            failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidatePageUrl(
        string value,
        string optionName,
        string expectedPath,
        ICollection<string> failures)
    {

        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumVerificationPageUrlLength
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !uri.IsLoopback
            || (!string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.Ordinal)
                && !string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.Ordinal))
            || !string.Equals(
                uri.AbsolutePath,
                expectedPath,
                StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            failures.Add(
                $"{DevelopmentEmailVerificationDeliveryOptions.SectionName}:{optionName} must be an absolute HTTP(S) loopback URI for {expectedPath} without a query, fragment, or user information.");
        }
    }
}
