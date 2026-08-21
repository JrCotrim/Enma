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
    public async Task DeliverAsync_ValidToken_LogsExactUsableLocalUrl()
    {
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<DevelopmentEmailVerificationDelivery>();
        var delivery = new DevelopmentEmailVerificationDelivery(
            Options.Create(new DevelopmentEmailVerificationDeliveryOptions()),
            logger);

        EmailVerificationDeliveryResult result = await delivery.DeliverAsync(
            Recipient,
            SyntheticToken);

        Assert.Equal(EmailVerificationDeliveryResult.Delivered, result);
        MailKitEmailVerificationDeliveryTests.LogEntry entry =
            Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(2003, entry.EventId.Id);
        Assert.Equal(
            $"DEVELOPMENT ONLY - verify the email with this local URL: http://localhost:5173/verify-email#token={SyntheticToken}",
            entry.Message);
        Assert.DoesNotContain(Recipient, entry.Message, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task DeliverAsync_MalformedToken_ThrowsWithoutLoggingOrEchoingToken()
    {
        const string malformedToken = "malformed-token";
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<DevelopmentEmailVerificationDelivery>();
        var delivery = new DevelopmentEmailVerificationDelivery(
            Options.Create(new DevelopmentEmailVerificationDeliveryOptions()),
            logger);

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
            .CapturingLogger<DevelopmentEmailVerificationDelivery>();
        var delivery = new DevelopmentEmailVerificationDelivery(
            Options.Create(new DevelopmentEmailVerificationDeliveryOptions()),
            logger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => delivery.DeliverAsync(
                Recipient,
                SyntheticToken,
                cancellation.Token));

        Assert.Empty(logger.Entries);
    }
}
