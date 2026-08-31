using System.Globalization;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using Enma.Application.Organizations.Invitations;
using Enma.Domain.Organizations;
using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Enma.Infrastructure.Email;

public sealed class MailKitOrganizationInvitationDelivery
    : IOrganizationInvitationDelivery
{
    public const string Subject = "Você recebeu um convite para o ENMA";

    private static readonly Action<ILogger, Exception?> LogAccepted =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(2010, "OrganizationInvitationDeliveryAccepted"),
            "Organization invitation message accepted by SMTP provider.");

    private static readonly Action<ILogger, Exception?> LogFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2011, "OrganizationInvitationDeliveryFailed"),
            "Organization invitation message delivery failed.");

    private readonly EmailVerificationDeliveryOptions options;
    private readonly OrganizationInvitationLinkBuilder linkBuilder;
    private readonly ILogger<MailKitOrganizationInvitationDelivery> logger;

    public MailKitOrganizationInvitationDelivery(
        IOptions<EmailVerificationDeliveryOptions> options,
        OrganizationInvitationLinkBuilder linkBuilder,
        ILogger<MailKitOrganizationInvitationDelivery> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(linkBuilder);
        ArgumentNullException.ThrowIfNull(logger);

        this.options = options.Value;
        this.linkBuilder = linkBuilder;
        this.logger = logger;
    }

    public async Task<OrganizationInvitationDeliveryResult> DeliverAsync(
        OrganizationInvitationDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var smtpClient = new SmtpClient();

        try
        {
            MimeMessage message = CreateMessage(
                request,
                linkBuilder.Build(request.RawToken));
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

            return OrganizationInvitationDeliveryResult.Accepted;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedDeliveryFailure(exception))
        {
            LogFailed(logger, null);
            return OrganizationInvitationDeliveryResult.Failed;
        }
    }

    private MimeMessage CreateMessage(
        OrganizationInvitationDeliveryRequest request,
        Uri invitationUri)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            options.SenderName,
            options.SenderAddress));
        message.To.Add(MailboxAddress.Parse(request.Email));
        message.Subject = Subject;

        string invitationLink = invitationUri.AbsoluteUri;
        string role = MapRole(request.Role);
        string expiresAt = request.ExpiresAt
            .ToUniversalTime()
            .ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
        string encodedInvitationLink = HtmlEncoder.Default.Encode(invitationLink);
        string encodedOrganizationName = HtmlEncoder.Default.Encode(
            request.OrganizationName);
        string encodedRole = HtmlEncoder.Default.Encode(role);
        string encodedExpiresAt = HtmlEncoder.Default.Encode(expiresAt);
        var bodyBuilder = new BodyBuilder
        {
            TextBody = $$"""
                Olá,

                Você foi convidado para participar da organização {{request.OrganizationName}} no ENMA como {{role}}.

                Aceite o convite usando o link abaixo:
                {{invitationLink}}

                Este convite expira em {{expiresAt}}.

                Se você não esperava este convite, ignore esta mensagem.
                """,
            HtmlBody = $$"""
                <!doctype html>
                <html lang="pt-BR">
                <body>
                  <p>Olá,</p>
                  <p>Você foi convidado para participar da organização <strong>{{encodedOrganizationName}}</strong> no ENMA como {{encodedRole}}.</p>
                  <p><a href="{{encodedInvitationLink}}">Aceitar convite</a></p>
                  <p>Este convite expira em {{encodedExpiresAt}}.</p>
                  <p>Se você não esperava este convite, ignore esta mensagem.</p>
                </body>
                </html>
                """
        };

        message.Body = bodyBuilder.ToMessageBody();
        return message;
    }

    private static string MapRole(OrganizationRole role)
    {
        return role switch
        {
            OrganizationRole.Administrator => "Administrador",
            OrganizationRole.Member => "Membro",
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
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
            or ParseException
            or SocketException
            or IOException;
    }
}
