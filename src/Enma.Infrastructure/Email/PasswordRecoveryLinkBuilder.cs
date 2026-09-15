using Microsoft.Extensions.Options;

namespace Enma.Infrastructure.Email;

public sealed class PasswordRecoveryLinkBuilder
{
    private const int RawTokenLength = 43;
    private readonly Uri pageUri;

    public PasswordRecoveryLinkBuilder(IOptions<EmailVerificationDeliveryOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        pageUri = new Uri(options.Value.PasswordRecoveryPageUrl, UriKind.Absolute);
    }

    internal PasswordRecoveryLinkBuilder(Uri pageUri)
    {
        ArgumentNullException.ThrowIfNull(pageUri);
        this.pageUri = pageUri;
    }

    public Uri Build(string rawToken)
    {
        if (rawToken is null || rawToken.Length != RawTokenLength || rawToken.Any(character =>
                character is not (>= 'A' and <= 'Z') and
                not (>= 'a' and <= 'z') and
                not (>= '0' and <= '9') and
                not '-' and not '_'))
        {
            throw new ArgumentException(
                "The password recovery token has an invalid format.",
                nameof(rawToken));
        }

        return new UriBuilder(pageUri) { Fragment = $"token={rawToken}" }.Uri;
    }
}
