using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Enma.Application.Authentication;
using Enma.Infrastructure.Email;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Enma.IntegrationTests.Infrastructure.Email;

public sealed class MailpitEmailVerificationDeliveryTests : IAsyncLifetime
{
    private const ushort SmtpContainerPort = 1025;
    private const ushort ApiContainerPort = 8025;
    private const string MailpitImage = "axllent/mailpit:v1.30.7";
    private const string Recipient = "recipient@example.test";
    private const string SyntheticToken =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno-_";

    private readonly IContainer container = new ContainerBuilder(MailpitImage)
        .WithPortBinding(SmtpContainerPort, true)
        .WithPortBinding(ApiContainerPort, true)
        .WithWaitStrategy(
            Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request
                    .ForPort(ApiContainerPort)
                    .ForPath("/api/v1/messages")))
        .Build();

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await container.StartAsync(timeout.Token);
    }

    [Fact]
    public async Task DeliverAsync_MailpitSmtp_ReceivesExpectedMultipartMessage()
    {
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<MailKitEmailVerificationDelivery>();
        EmailVerificationDeliveryOptions options =
            MailKitEmailVerificationDeliveryTests.CreateOptions(
                container.GetMappedPublicPort(SmtpContainerPort),
                includeCredentials: false);
        MailKitEmailVerificationDelivery delivery =
            MailKitEmailVerificationDeliveryTests.CreateDelivery(options, logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        EmailVerificationDeliveryResult result = await delivery.DeliverAsync(
            Recipient,
            SyntheticToken,
            timeout.Token);
        MimeMessage message = await GetLatestMessageAsync(timeout.Token);

        Assert.Equal(EmailVerificationDeliveryResult.Delivered, result);
        MailboxAddress from = Assert.IsType<MailboxAddress>(Assert.Single(message.From));
        MailboxAddress to = Assert.IsType<MailboxAddress>(Assert.Single(message.To));
        Assert.Equal("ENMA", from.Name);
        Assert.Equal("no-reply@example.test", from.Address);
        Assert.Equal(Recipient, to.Address);
        Assert.Equal(MailKitEmailVerificationDelivery.Subject, message.Subject);
        Assert.IsType<MultipartAlternative>(message.Body);
        Assert.NotNull(message.TextBody);
        Assert.NotNull(message.HtmlBody);
        Assert.Contains($"#token={SyntheticToken}", message.TextBody);
        Assert.Contains($"#token={SyntheticToken}", message.HtmlBody);
        Assert.Equal(1, CountOccurrences(message.TextBody, SyntheticToken));
        Assert.Equal(1, CountOccurrences(message.HtmlBody, SyntheticToken));
        Assert.DoesNotContain("?token=", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("?token=", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain($"/{SyntheticToken}", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain($"/{SyntheticToken}", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticToken, message.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain(Recipient, message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Recipient, message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp-user", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp-user", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "synthetic-smtp-password",
            message.TextBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "synthetic-smtp-password",
            message.HtmlBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            message.Headers,
            header => header.Value.Contains(SyntheticToken, StringComparison.Ordinal));
        Assert.DoesNotContain("<script", message.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", message.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", message.HtmlBody, StringComparison.OrdinalIgnoreCase);

        MailKitEmailVerificationDeliveryTests.LogEntry logEntry =
            Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, logEntry.Level);
        Assert.Equal(2000, logEntry.EventId.Id);
        Assert.Null(logEntry.Exception);
        MailKitEmailVerificationDeliveryTests.AssertNoSensitiveTelemetry(logger.Entries);
    }

    private static int CountOccurrences(string value, string searchValue)
    {
        int count = 0;
        int startIndex = 0;

        while ((startIndex = value.IndexOf(
            searchValue,
            startIndex,
            StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += searchValue.Length;
        }

        return count;
    }

    private async Task<MimeMessage> GetLatestMessageAsync(
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(
                $"http://{container.Hostname}:{container.GetMappedPublicPort(ApiContainerPort)}/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        await using Stream rawMessage = await httpClient.GetStreamAsync(
            "api/v1/message/latest/raw",
            cancellationToken);

        return await MimeMessage.LoadAsync(rawMessage, cancellationToken);
    }

    public async Task DisposeAsync()
    {
        await container.DisposeAsync();
    }
}
