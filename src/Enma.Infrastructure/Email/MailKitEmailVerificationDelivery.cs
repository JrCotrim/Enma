using System.Net.Sockets;
using System.Text.Encodings.Web;
using Enma.Application.Authentication;
using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Enma.Infrastructure.Email;

public sealed class MailKitEmailVerificationDelivery
    : IEmailVerificationDelivery
{
    public const string Subject = "Confirme seu e-mail no ENMA";

    private static readonly Action<ILogger, Exception?> LogAccepted =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(2000, "EmailVerificationDeliveryAccepted"),
            "Email verification message accepted by SMTP provider.");

    private static readonly Action<ILogger, Exception?> LogFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2001, "EmailVerificationDeliveryFailed"),
            "Email verification message delivery failed.");

    private readonly EmailVerificationDeliveryOptions options;
    private readonly EmailVerificationLinkBuilder linkBuilder;
    private readonly ILogger<MailKitEmailVerificationDelivery> logger;

    public MailKitEmailVerificationDelivery(
        IOptions<EmailVerificationDeliveryOptions> options,
        EmailVerificationLinkBuilder linkBuilder,
        ILogger<MailKitEmailVerificationDelivery> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(linkBuilder);
        ArgumentNullException.ThrowIfNull(logger);

        this.options = options.Value;
        this.linkBuilder = linkBuilder;
        this.logger = logger;
    }

    public async Task<EmailVerificationDeliveryResult> DeliverAsync(
        string email,
        string rawToken,
        CancellationToken cancellationToken = default)
    {
        MimeMessage message = CreateMessage(email, linkBuilder.Build(rawToken));

        using var smtpClient = new SmtpClient();

        try
        {
            await smtpClient.ConnectAsync(
                options.SmtpHost,
                options.SmtpPort,
                options.SmtpSecurity,
                cancellationToken);

            if (!string.IsNullOrEmpty(options.SmtpUsername)
                || !string.IsNullOrEmpty(options.SmtpPassword))
            {
                await smtpClient.AuthenticateAsync(
                    options.SmtpUsername,
                    options.SmtpPassword,
                    cancellationToken);
            }

            await smtpClient.SendAsync(message, cancellationToken);
            await smtpClient.DisconnectAsync(true, cancellationToken);

            LogAccepted(logger, null);

            return EmailVerificationDeliveryResult.Delivered;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedDeliveryFailure(exception))
        {
            LogFailed(logger, null);

            return EmailVerificationDeliveryResult.Failed;
        }
    }

    private MimeMessage CreateMessage(string email, Uri verificationUri)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.SenderName, options.SenderAddress));
        message.To.Add(MailboxAddress.Parse(email));
        message.Subject = Subject;

        string verificationLink = verificationUri.AbsoluteUri;
        string encodedVerificationLink = HtmlEncoder.Default.Encode(verificationLink);
        var bodyBuilder = new BodyBuilder
        {
            TextBody = $$"""
                Olá,

                Confirme seu e-mail no ENMA usando o link abaixo:
                {{verificationLink}}

                Este link expira em 1 hora.

                Se você não fez esta solicitação, ignore esta mensagem.
                """,
            HtmlBody = $$"""
                <!doctype html>
                <html lang="pt-BR">
                <body>
                  <p>Olá,</p>
                  <p>Confirme seu e-mail no ENMA usando o link abaixo:</p>
                  <p><a href="{{encodedVerificationLink}}">Confirmar meu e-mail</a></p>
                  <p>Este link expira em 1 hora.</p>
                  <p>Se você não fez esta solicitação, ignore esta mensagem.</p>
                </body>
                </html>
                """
        };

        message.Body = bodyBuilder.ToMessageBody();

        return message;
    }

    private static bool IsExpectedDeliveryFailure(Exception exception)
    {
        return exception is SmtpCommandException
            or SmtpProtocolException
            or ServiceNotConnectedException
            or ServiceNotAuthenticatedException
            or MailKit.Security.AuthenticationException
            or MailKit.Security.SaslException
            or MailKit.Security.SslHandshakeException
            or SocketException
            or IOException;
    }
}
