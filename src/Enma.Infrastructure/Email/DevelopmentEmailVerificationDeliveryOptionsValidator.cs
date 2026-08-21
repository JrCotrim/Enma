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

        string value = options.VerificationPageUrl;

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
                "/verify-email",
                StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return ValidateOptionsResult.Fail(
                $"{DevelopmentEmailVerificationDeliveryOptions.SectionName}:VerificationPageUrl must be an absolute HTTP(S) loopback URI for /verify-email without a query, fragment, or user information.");
        }

        return ValidateOptionsResult.Success;
    }
}
