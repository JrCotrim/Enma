using Microsoft.Extensions.Options;

namespace Enma.Infrastructure.Email;

public sealed class EmailVerificationLinkBuilder
{
    private const int RawTokenLength = 43;
    private readonly Uri verificationPageUri;

    public EmailVerificationLinkBuilder(
        IOptions<EmailVerificationDeliveryOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        verificationPageUri = new Uri(
            options.Value.VerificationPageUrl,
            UriKind.Absolute);
    }

    internal EmailVerificationLinkBuilder(Uri verificationPageUri)
    {
        ArgumentNullException.ThrowIfNull(verificationPageUri);

        this.verificationPageUri = verificationPageUri;
    }

    public Uri Build(string rawToken)
    {
        if (!IsValidRawToken(rawToken))
        {
            throw new ArgumentException(
                "The email verification token has an invalid format.",
                nameof(rawToken));
        }

        return new UriBuilder(verificationPageUri)
        {
            Fragment = $"token={rawToken}"
        }.Uri;
    }

    private static bool IsValidRawToken(string? rawToken)
    {
        if (rawToken is null || rawToken.Length != RawTokenLength)
        {
            return false;
        }

        foreach (char character in rawToken)
        {
            if ((character is >= 'A' and <= 'Z')
                || (character is >= 'a' and <= 'z')
                || (character is >= '0' and <= '9')
                || character is '-' or '_')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
