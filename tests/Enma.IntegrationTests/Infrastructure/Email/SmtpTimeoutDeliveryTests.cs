using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Enma.Application.Authentication;
using Enma.Application.Organizations.Invitations;
using Enma.Infrastructure.Email;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Infrastructure.Email;

// A test-owned SMTP endpoint accepts the connection and never sends the
// greeting, so every delivery can only end through the configured timeout.
public sealed class SmtpTimeoutDeliveryTests
{
    private const string Recipient = "recipient@example.test";
    private const string SyntheticToken =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno-_";
    private const int SmtpTimeoutSeconds = 1;
    private static readonly TimeSpan MaximumElapsed = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task EmailVerification_SilentSmtp_ReturnsFailedAfterTimeout()
    {
        await using var server = SilentSmtpServer.Start();
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<MailKitEmailVerificationDelivery>();
        IOptions<EmailVerificationDeliveryOptions> options =
            CreateOptions(server.Port);
        var delivery = new MailKitEmailVerificationDelivery(
            options,
            new EmailVerificationLinkBuilder(options),
            logger);
        var stopwatch = Stopwatch.StartNew();

        EmailVerificationDeliveryResult result = await delivery.DeliverAsync(
            Recipient,
            SyntheticToken,
            server.TestTimeout);

        Assert.Equal(EmailVerificationDeliveryResult.Failed, result);
        Assert.True(stopwatch.Elapsed < MaximumElapsed);
        AssertSanitizedFailure(logger.Entries, expectedEventId: 2001);
    }

    [Fact]
    public async Task PasswordRecovery_SilentSmtp_ReturnsFailedAfterTimeout()
    {
        await using var server = SilentSmtpServer.Start();
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<MailKitPasswordRecoveryDelivery>();
        IOptions<EmailVerificationDeliveryOptions> options =
            CreateOptions(server.Port);
        var delivery = new MailKitPasswordRecoveryDelivery(
            options,
            new PasswordRecoveryLinkBuilder(options),
            logger);
        var stopwatch = Stopwatch.StartNew();

        PasswordRecoveryDeliveryResult result = await delivery.DeliverAsync(
            Recipient,
            SyntheticToken,
            server.TestTimeout);

        Assert.Equal(PasswordRecoveryDeliveryResult.Failed, result);
        Assert.True(stopwatch.Elapsed < MaximumElapsed);
        AssertSanitizedFailure(logger.Entries, expectedEventId: 2011);
    }

    [Fact]
    public async Task OrganizationInvitation_SilentSmtp_ReturnsFailedAfterTimeout()
    {
        await using var server = SilentSmtpServer.Start();
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<MailKitOrganizationInvitationDelivery>();
        IOptions<EmailVerificationDeliveryOptions> options =
            CreateOptions(server.Port);
        var delivery = new MailKitOrganizationInvitationDelivery(
            options,
            new OrganizationInvitationLinkBuilder(options),
            logger);
        OrganizationInvitationDeliveryRequest request =
            OrganizationInvitationDeliveryTests.CreateRequest();
        var stopwatch = Stopwatch.StartNew();

        OrganizationInvitationDeliveryResult result = await delivery.DeliverAsync(
            request,
            server.TestTimeout);

        Assert.Equal(OrganizationInvitationDeliveryResult.Failed, result);
        Assert.True(stopwatch.Elapsed < MaximumElapsed);
        AssertSanitizedFailure(
            logger.Entries,
            expectedEventId: 2011,
            request.Email);
    }

    [Fact]
    public async Task DevelopmentDelivery_SilentSmtp_UsesConfiguredTimeout()
    {
        await using var server = SilentSmtpServer.Start();
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<MailKitEmailVerificationDelivery>();
        var delivery = new DevelopmentEmailVerificationDelivery(
            Options.Create(new DevelopmentEmailVerificationDeliveryOptions
            {
                SmtpPort = server.Port,
                SmtpTimeoutSeconds = SmtpTimeoutSeconds
            }),
            logger);
        var stopwatch = Stopwatch.StartNew();

        EmailVerificationDeliveryResult result = await delivery.DeliverAsync(
            Recipient,
            SyntheticToken,
            server.TestTimeout);

        Assert.Equal(EmailVerificationDeliveryResult.Failed, result);
        Assert.True(stopwatch.Elapsed < MaximumElapsed);
        AssertSanitizedFailure(logger.Entries, expectedEventId: 2001);
    }

    [Fact]
    public void DefaultTimeout_IsTenSeconds()
    {
        Assert.Equal(10, new EmailVerificationDeliveryOptions().SmtpTimeoutSeconds);
        Assert.Equal(
            10,
            new DevelopmentEmailVerificationDeliveryOptions().SmtpTimeoutSeconds);
    }

    private static IOptions<EmailVerificationDeliveryOptions> CreateOptions(
        int smtpPort)
    {
        EmailVerificationDeliveryOptions options =
            MailKitEmailVerificationDeliveryTests.CreateOptions(
                smtpPort,
                includeCredentials: true);

        return Options.Create(new EmailVerificationDeliveryOptions
        {
            VerificationPageUrl = options.VerificationPageUrl,
            PasswordRecoveryPageUrl = options.PasswordRecoveryPageUrl,
            SenderName = options.SenderName,
            SenderAddress = options.SenderAddress,
            SmtpHost = options.SmtpHost,
            SmtpPort = options.SmtpPort,
            SmtpSecurity = options.SmtpSecurity,
            SmtpUsername = options.SmtpUsername,
            SmtpPassword = options.SmtpPassword,
            SmtpTimeoutSeconds = SmtpTimeoutSeconds
        });
    }

    private static void AssertSanitizedFailure(
        IReadOnlyList<MailKitEmailVerificationDeliveryTests.LogEntry> entries,
        int expectedEventId,
        string recipient = Recipient)
    {
        MailKitEmailVerificationDeliveryTests.LogEntry entry = Assert.Single(entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(expectedEventId, entry.EventId.Id);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(recipient, entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SyntheticToken, entry.Message, StringComparison.Ordinal);
        MailKitEmailVerificationDeliveryTests.AssertNoSensitiveTelemetry(entries);
    }

    internal sealed class SilentSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource testTimeout;
        private readonly Task acceptTask;

        private SilentSmtpServer(TcpListener listener)
        {
            this.listener = listener;
            testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            acceptTask = HoldConnectionsAsync(testTimeout.Token);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public CancellationToken TestTimeout => testTimeout.Token;

        public static SilentSmtpServer Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new SilentSmtpServer(listener);
        }

        public async ValueTask DisposeAsync()
        {
            await testTimeout.CancelAsync();
            listener.Stop();

            try
            {
                await acceptTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            testTimeout.Dispose();
        }

        private async Task HoldConnectionsAsync(CancellationToken cancellationToken)
        {
            var clients = new List<TcpClient>();

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    clients.Add(await listener.AcceptTcpClientAsync(
                        cancellationToken));
                }
            }
            finally
            {
                foreach (TcpClient client in clients)
                {
                    client.Dispose();
                }
            }
        }
    }
}
