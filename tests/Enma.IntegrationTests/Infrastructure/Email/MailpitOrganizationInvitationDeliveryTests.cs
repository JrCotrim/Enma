using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Enma.Application.Organizations.Invitations;
using Enma.Infrastructure.Email;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Enma.IntegrationTests.Infrastructure.Email;

public sealed class MailpitOrganizationInvitationDeliveryTests : IAsyncLifetime
{
    private const ushort SmtpContainerPort = 1025;
    private const ushort ApiContainerPort = 8025;
    private const string MailpitImage = "axllent/mailpit:v1.30.7";
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
    public async Task DeliverAsync_Mailpit_ReceivesSafeMultipartInvitation()
    {
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<MailKitOrganizationInvitationDelivery>();
        MailKitOrganizationInvitationDelivery delivery =
            OrganizationInvitationDeliveryTests.CreateMailKitDelivery(
                container.GetMappedPublicPort(SmtpContainerPort),
                logger);
        OrganizationInvitationDeliveryRequest request =
            OrganizationInvitationDeliveryTests.CreateRequest();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        OrganizationInvitationDeliveryResult result = await delivery.DeliverAsync(
            request,
            timeout.Token);
        MimeMessage message = await GetLatestMessageAsync(timeout.Token);

        Assert.Equal(OrganizationInvitationDeliveryResult.Accepted, result);
        Assert.Equal(
            MailKitOrganizationInvitationDelivery.Subject,
            message.Subject);
        Assert.IsType<MultipartAlternative>(message.Body);
        string textBody = Assert.IsType<string>(message.TextBody);
        string htmlBody = Assert.IsType<string>(message.HtmlBody);
        Assert.Contains(request.OrganizationName, textBody);
        Assert.Contains("Administrador", textBody);
        Assert.Contains("Administrador", htmlBody);
        Assert.Contains(
            $"https://app.example/accept-invitation#token={SyntheticToken}",
            textBody);
        Assert.Contains($"#token={SyntheticToken}", htmlBody);
        Assert.Equal(1, CountOccurrences(textBody, SyntheticToken));
        Assert.Equal(1, CountOccurrences(htmlBody, SyntheticToken));
        Assert.DoesNotContain("?token=", textBody, StringComparison.Ordinal);
        Assert.DoesNotContain("?token=", htmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain($"/{SyntheticToken}", textBody, StringComparison.Ordinal);
        Assert.DoesNotContain($"/{SyntheticToken}", htmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticToken, message.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain(
            message.Headers,
            header => header.Value.Contains(SyntheticToken, StringComparison.Ordinal));

        MailKitEmailVerificationDeliveryTests.LogEntry entry = Assert.Single(
            logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(2010, entry.EventId.Id);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(SyntheticToken, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Email, entry.Message, StringComparison.Ordinal);
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

    public async Task DisposeAsync()
    {
        await container.DisposeAsync();
    }
}
