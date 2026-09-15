using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Enma.Application.Authentication;
using Enma.Infrastructure.Email;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Enma.IntegrationTests.Infrastructure.Email;

public sealed class MailpitPasswordRecoveryDeliveryTests : IAsyncLifetime
{
    private const ushort SmtpPort = 1025;
    private const ushort ApiPort = 8025;
    private const string Token = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno-_";
    private readonly IContainer container = new ContainerBuilder("axllent/mailpit:v1.30.7")
        .WithPortBinding(SmtpPort, true)
        .WithPortBinding(ApiPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
            request => request.ForPort(ApiPort).ForPath("/api/v1/messages")))
        .Build();

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await container.StartAsync(timeout.Token);
    }

    [Fact]
    public async Task DeliverAsyncSendsFragmentLinkWithoutSensitiveTelemetry()
    {
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<MailKitPasswordRecoveryDelivery>();
        EmailVerificationDeliveryOptions options =
            MailKitEmailVerificationDeliveryTests.CreateOptions(
                container.GetMappedPublicPort(SmtpPort),
                includeCredentials: false);
        IOptions<EmailVerificationDeliveryOptions> wrapped = Options.Create(options);
        var delivery = new MailKitPasswordRecoveryDelivery(
            wrapped,
            new PasswordRecoveryLinkBuilder(wrapped),
            logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        PasswordRecoveryDeliveryResult result = await delivery.DeliverAsync(
            "recipient@example.test",
            Token,
            timeout.Token);
        MimeMessage message = await GetLatestMessageAsync(timeout.Token);

        Assert.Equal(PasswordRecoveryDeliveryResult.Delivered, result);
        Assert.Equal(MailKitPasswordRecoveryDelivery.Subject, message.Subject);
        Assert.Contains($"https://app.example/reset-password#token={Token}", message.TextBody);
        Assert.Contains($"#token={Token}", message.HtmlBody);
        Assert.DoesNotContain("?token=", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, message.Subject, StringComparison.Ordinal);
        MailKitEmailVerificationDeliveryTests.LogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(2010, entry.EventId.Id);
        Assert.DoesNotContain(Token, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("recipient@example.test", entry.Message, StringComparison.Ordinal);
    }

    private async Task<MimeMessage> GetLatestMessageAsync(CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(
                $"http://{container.Hostname}:{container.GetMappedPublicPort(ApiPort)}/")
        };
        await using Stream rawMessage = await httpClient.GetStreamAsync(
            "api/v1/message/latest/raw",
            cancellationToken);
        return await MimeMessage.LoadAsync(rawMessage, cancellationToken);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();
}
