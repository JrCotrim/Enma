using System.Net.Sockets;
using System.Text.Encodings.Web;
using Enma.Application.Authentication;
using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Enma.Infrastructure.Email;

public sealed class MailKitPasswordRecoveryDelivery : IPasswordRecoveryDelivery
{
    public const string Subject = "Redefina sua senha no ENMA";

    private static readonly Action<ILogger, Exception?> LogAccepted = LoggerMessage.Define(
        LogLevel.Information,
        new EventId(2010, "PasswordRecoveryDeliveryAccepted"),
        "Password recovery message accepted by SMTP provider.");
    private static readonly Action<ILogger, Exception?> LogFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2011, "PasswordRecoveryDeliveryFailed"),
        "Password recovery message delivery failed.");

    private readonly EmailVerificationDeliveryOptions options;
    private readonly PasswordRecoveryLinkBuilder linkBuilder;
    private readonly ILogger<MailKitPasswordRecoveryDelivery> logger;

    public MailKitPasswordRecoveryDelivery(
        IOptions<EmailVerificationDeliveryOptions> options,
        PasswordRecoveryLinkBuilder linkBuilder,
        ILogger<MailKitPasswordRecoveryDelivery> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(linkBuilder);
        ArgumentNullException.ThrowIfNull(logger);

        this.options = options.Value;
        this.linkBuilder = linkBuilder;
        this.logger = logger;
    }

    public async Task<PasswordRecoveryDeliveryResult> DeliverAsync(
        string email,
        string rawToken,
        CancellationToken cancellationToken = default)
    {
        if (!MailboxAddress.TryParse(email, out MailboxAddress? recipient))
        {
            LogFailed(logger, null);
            return PasswordRecoveryDeliveryResult.Failed;
        }

        MimeMessage message = CreateMessage(recipient, linkBuilder.Build(rawToken));
        using var smtpClient = new SmtpClient();

        try
        {
            await smtpClient.ConnectAsync(
                options.SmtpHost,
                options.SmtpPort,
                options.SmtpSecurity,
                cancellationToken);

            if (!string.IsNullOrEmpty(options.SmtpUsername) ||
                !string.IsNullOrEmpty(options.SmtpPassword))
            {
                await smtpClient.AuthenticateAsync(
                    options.SmtpUsername,
                    options.SmtpPassword,
                    cancellationToken);
            }

            await smtpClient.SendAsync(message, cancellationToken);
            await smtpClient.DisconnectAsync(true, cancellationToken);
            LogAccepted(logger, null);
            return PasswordRecoveryDeliveryResult.Delivered;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            SmtpCommandException or
            SmtpProtocolException or
            ServiceNotConnectedException or
            ServiceNotAuthenticatedException or
            MailKit.Security.AuthenticationException or
            MailKit.Security.SaslException or
            MailKit.Security.SslHandshakeException or
            SocketException or
            IOException)
        {
            LogFailed(logger, null);
            return PasswordRecoveryDeliveryResult.Failed;
        }
    }

    private MimeMessage CreateMessage(MailboxAddress recipient, Uri recoveryUri)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.SenderName, options.SenderAddress));
        message.To.Add(recipient);
        message.Subject = Subject;

        string recoveryLink = recoveryUri.AbsoluteUri;
        string encodedRecoveryLink = HtmlEncoder.Default.Encode(recoveryLink);
        var bodyBuilder = new BodyBuilder
        {
            TextBody = $$"""
                Olá,

                Redefina sua senha no ENMA usando o link abaixo:
                {{recoveryLink}}

                Este link expira em 1 hora e só pode ser usado uma vez.

                Se você não fez esta solicitação, ignore esta mensagem.
                """,
            HtmlBody = $$"""
                <!doctype html>
                <html lang="pt-BR">
                <body>
                  <p>Olá,</p>
                  <p>Redefina sua senha no ENMA usando o link abaixo:</p>
                  <p><a href="{{encodedRecoveryLink}}">Redefinir minha senha</a></p>
                  <p>Este link expira em 1 hora e só pode ser usado uma vez.</p>
                  <p>Se você não fez esta solicitação, ignore esta mensagem.</p>
                </body>
                </html>
                """
        };
        message.Body = bodyBuilder.ToMessageBody();
        return message;
    }
}
