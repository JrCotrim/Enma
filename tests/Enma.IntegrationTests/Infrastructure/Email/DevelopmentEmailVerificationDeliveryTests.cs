using System.Net;
using System.Net.Sockets;
using Enma.Application.Authentication;
using Enma.Infrastructure.Email;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Infrastructure.Email;

public sealed class DevelopmentEmailVerificationDeliveryTests
{
    private const string Recipient = "developer@example.test";
    private const string SyntheticToken =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno-_";

    [Fact]
    public async Task DeliverAsync_ConnectionFailure_ReturnsFailedWithSafeLog()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<MailKitEmailVerificationDelivery>();
        DevelopmentEmailVerificationDelivery delivery = CreateDelivery(port, logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task server = AcceptAndCloseAsync(listener, timeout.Token);

        try
        {
            EmailVerificationDeliveryResult result = await delivery.DeliverAsync(
                Recipient,
                SyntheticToken,
                timeout.Token);
            await server;

            Assert.Equal(EmailVerificationDeliveryResult.Failed, result);
            MailKitEmailVerificationDeliveryTests.LogEntry entry =
                Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Equal(2001, entry.EventId.Id);
            Assert.Null(entry.Exception);
            Assert.DoesNotContain(
                SyntheticToken,
                entry.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(Recipient, entry.Message, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task DeliverAsync_MalformedToken_ThrowsWithoutLoggingOrEchoingToken()
    {
        const string malformedToken = "malformed-token";
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<MailKitEmailVerificationDelivery>();
        DevelopmentEmailVerificationDelivery delivery = CreateDelivery(25, logger);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => delivery.DeliverAsync(Recipient, malformedToken));

        Assert.DoesNotContain(
            malformedToken,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task DeliverAsync_CancelledToken_DoesNotLogVerificationUrl()
    {
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<MailKitEmailVerificationDelivery>();
        DevelopmentEmailVerificationDelivery delivery = CreateDelivery(25, logger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => delivery.DeliverAsync(
                Recipient,
                SyntheticToken,
                cancellation.Token));

        Assert.Empty(logger.Entries);
    }

    private static async Task AcceptAndCloseAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(
            cancellationToken);
    }

    internal static DevelopmentEmailVerificationDelivery CreateDelivery(
        int smtpPort,
        MailKitEmailVerificationDeliveryTests.CapturingLogger<
            MailKitEmailVerificationDelivery> logger)
    {
        return new DevelopmentEmailVerificationDelivery(
            Options.Create(new DevelopmentEmailVerificationDeliveryOptions()),
            logger,
            smtpPort);
    }
}
