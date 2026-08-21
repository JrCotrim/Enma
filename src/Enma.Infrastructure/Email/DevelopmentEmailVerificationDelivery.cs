using Enma.Application.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enma.Infrastructure.Email;

public sealed class DevelopmentEmailVerificationDelivery
    : IEmailVerificationDelivery
{
    private static readonly Action<ILogger, string, Exception?>
        LogVerificationUrl = LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2003, "DevelopmentEmailVerificationUrl"),
            "DEVELOPMENT ONLY - verify the email with this local URL: {VerificationUrl}");

    private readonly EmailVerificationLinkBuilder linkBuilder;
    private readonly ILogger<DevelopmentEmailVerificationDelivery> logger;

    public DevelopmentEmailVerificationDelivery(
        IOptions<DevelopmentEmailVerificationDeliveryOptions> options,
        ILogger<DevelopmentEmailVerificationDelivery> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        linkBuilder = new EmailVerificationLinkBuilder(new Uri(
            options.Value.VerificationPageUrl,
            UriKind.Absolute));
        this.logger = logger;
    }

    public Task<EmailVerificationDeliveryResult> DeliverAsync(
        string email,
        string rawToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        cancellationToken.ThrowIfCancellationRequested();

        Uri verificationUri = linkBuilder.Build(rawToken);
        LogVerificationUrl(logger, verificationUri.AbsoluteUri, null);

        return Task.FromResult(EmailVerificationDeliveryResult.Delivered);
    }
}
