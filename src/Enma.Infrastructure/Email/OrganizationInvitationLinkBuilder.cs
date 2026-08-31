using Microsoft.Extensions.Options;

namespace Enma.Infrastructure.Email;

public sealed class OrganizationInvitationLinkBuilder
{
    private const int RawTokenLength = 43;
    private readonly Uri invitationPageUri;

    public OrganizationInvitationLinkBuilder(
        IOptions<EmailVerificationDeliveryOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        invitationPageUri = CreateInvitationPageUri(
            new Uri(options.Value.VerificationPageUrl, UriKind.Absolute));
    }

    internal OrganizationInvitationLinkBuilder(Uri configuredPageUri)
    {
        ArgumentNullException.ThrowIfNull(configuredPageUri);
        invitationPageUri = CreateInvitationPageUri(configuredPageUri);
    }

    public Uri Build(string rawToken)
    {
        if (!IsValidRawToken(rawToken))
        {
            throw new ArgumentException(
                "The organization invitation token has an invalid format.",
                nameof(rawToken));
        }

        return new UriBuilder(invitationPageUri)
        {
            Fragment = $"token={rawToken}"
        }.Uri;
    }

    private static Uri CreateInvitationPageUri(Uri configuredPageUri)
    {
        return new UriBuilder(configuredPageUri)
        {
            Path = "/accept-invitation",
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;
    }

    private static bool IsValidRawToken(string? rawToken)
    {
        if (rawToken is null || rawToken.Length != RawTokenLength)
        {
            return false;
        }

        return rawToken.All(character =>
            character is >= 'A' and <= 'Z' or
                >= 'a' and <= 'z' or
                >= '0' and <= '9' or
                '-' or '_');
    }
}
