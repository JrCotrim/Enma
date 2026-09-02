using System.Net;
using System.Net.Sockets;
using Enma.Application.Organizations.Invitations;
using Enma.Domain.Organizations;
using Enma.Infrastructure.Email;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Infrastructure.Email;

public sealed class OrganizationInvitationDeliveryTests
{
    private const string SyntheticToken =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno-_";

    [Fact]
    public void LinkBuilder_PutsTokenOnlyInAcceptanceFragment()
    {
        EmailVerificationDeliveryOptions options =
            MailKitEmailVerificationDeliveryTests.CreateOptions(
                smtpPort: 25,
                includeCredentials: false);
        var builder = new OrganizationInvitationLinkBuilder(
            Options.Create(options));

        Uri uri = builder.Build(SyntheticToken);

        Assert.Equal("/accept-invitation", uri.AbsolutePath);
        Assert.Equal(string.Empty, uri.Query);
        Assert.Equal($"#token={SyntheticToken}", uri.Fragment);
        Assert.DoesNotContain(SyntheticToken, uri.AbsolutePath, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticToken, uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DevelopmentDelivery_ConnectionFailure_ReturnsFailedWithSafeLog()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<MailKitOrganizationInvitationDelivery>();
        DevelopmentOrganizationInvitationDelivery delivery =
            CreateDevelopmentDelivery(port, logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task server = AcceptAndCloseAsync(listener, timeout.Token);

        try
        {
            OrganizationInvitationDeliveryResult result = await delivery.DeliverAsync(
                CreateRequest(),
                timeout.Token);
            await server;

            Assert.Equal(OrganizationInvitationDeliveryResult.Failed, result);
            AssertSafeFailureLog(logger);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task DevelopmentDelivery_MalformedToken_ThrowsWithoutLoggingToken()
    {
        const string malformedToken = "malformed-token";
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<MailKitOrganizationInvitationDelivery>();
        DevelopmentOrganizationInvitationDelivery delivery =
            CreateDevelopmentDelivery(25, logger);
        OrganizationInvitationDeliveryRequest request = CreateRequest() with
        {
            RawToken = malformedToken
        };

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => delivery.DeliverAsync(request));

        Assert.DoesNotContain(
            malformedToken,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task DevelopmentDelivery_CancelledToken_DoesNotLogInvitationUrl()
    {
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<MailKitOrganizationInvitationDelivery>();
        DevelopmentOrganizationInvitationDelivery delivery =
            CreateDevelopmentDelivery(25, logger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => delivery.DeliverAsync(CreateRequest(), cancellation.Token));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task MailKitDelivery_ConnectionFailure_ReturnsFailedWithSafeLog()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<MailKitOrganizationInvitationDelivery>();
        MailKitOrganizationInvitationDelivery delivery = CreateMailKitDelivery(
            port,
            logger);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task server = AcceptAndCloseAsync(listener, timeout.Token);

        try
        {
            OrganizationInvitationDeliveryResult result = await delivery.DeliverAsync(
                CreateRequest(),
                timeout.Token);
            await server;

            Assert.Equal(OrganizationInvitationDeliveryResult.Failed, result);
            AssertSafeFailureLog(logger);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task AcceptAndCloseAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(
            cancellationToken);
    }

    internal static MailKitOrganizationInvitationDelivery CreateMailKitDelivery(
        int smtpPort,
        MailKitEmailVerificationDeliveryTests.CapturingLogger<
            MailKitOrganizationInvitationDelivery> logger)
    {
        EmailVerificationDeliveryOptions options =
            MailKitEmailVerificationDeliveryTests.CreateOptions(
                smtpPort,
                includeCredentials: false);
        IOptions<EmailVerificationDeliveryOptions> wrapped = Options.Create(options);
        return new MailKitOrganizationInvitationDelivery(
            wrapped,
            new OrganizationInvitationLinkBuilder(wrapped),
            logger);
    }

    internal static DevelopmentOrganizationInvitationDelivery
        CreateDevelopmentDelivery(
            int smtpPort,
            MailKitEmailVerificationDeliveryTests.CapturingLogger<
                MailKitOrganizationInvitationDelivery> logger)
    {
        return new DevelopmentOrganizationInvitationDelivery(
            Options.Create(new DevelopmentEmailVerificationDeliveryOptions()),
            logger,
            smtpPort);
    }

    private static void AssertSafeFailureLog(
        MailKitEmailVerificationDeliveryTests.CapturingLogger<
            MailKitOrganizationInvitationDelivery> logger)
    {
        MailKitEmailVerificationDeliveryTests.LogEntry entry = Assert.Single(
            logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(2011, entry.EventId.Id);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(
            SyntheticToken,
            entry.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            CreateRequest().Email,
            entry.Message,
            StringComparison.Ordinal);
    }

    internal static OrganizationInvitationDeliveryRequest CreateRequest()
    {
        return new OrganizationInvitationDeliveryRequest(
            "invited@example.test",
            "Organização Legal",
            OrganizationRole.Administrator,
            new DateTimeOffset(2026, 9, 6, 14, 0, 0, TimeSpan.Zero),
            SyntheticToken);
    }
}
