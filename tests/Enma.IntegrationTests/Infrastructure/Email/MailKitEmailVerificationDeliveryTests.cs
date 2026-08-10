using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Enma.Application.Authentication;
using Enma.Infrastructure.Email;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Infrastructure.Email;

public sealed class MailKitEmailVerificationDeliveryTests
{
    private const string Recipient = "recipient@example.test";
    private const string SyntheticPassword = "synthetic-smtp-password";
    private const string SyntheticToken =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno-_";

    [Fact]
    public async Task DeliverAsync_TestOwnedSmtpTerminatesConnection_ReturnsFailedWithSanitizedTelemetry()
    {
        var logger = new CapturingLogger<MailKitEmailVerificationDelivery>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        Task serverTask = Task.CompletedTask;

        try
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            MailKitEmailVerificationDelivery delivery = CreateDelivery(
                CreateOptions(port, includeCredentials: true),
                logger);
            serverTask = AcceptAndTerminateConnectionAsync(
                listener,
                timeout.Token);

            EmailVerificationDeliveryResult result = await delivery.DeliverAsync(
                Recipient,
                SyntheticToken,
                timeout.Token);
            await serverTask;

            Assert.Equal(EmailVerificationDeliveryResult.Failed, result);
            AssertSanitizedFailure(logger);
        }
        finally
        {
            await timeout.CancelAsync();
            listener.Stop();
            await DrainServerTaskAsync(serverTask, timeout.Token);
        }
    }

    [Fact]
    public async Task DeliverAsync_SmtpRejectsCredentials_ReturnsFailedWithSanitizedTelemetry()
    {
        var logger = new CapturingLogger<MailKitEmailVerificationDelivery>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        Task serverTask = Task.CompletedTask;

        try
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            MailKitEmailVerificationDelivery delivery = CreateDelivery(
                CreateOptions(port, includeCredentials: true),
                logger);
            serverTask = RejectAuthenticationAsync(listener, timeout.Token);

            EmailVerificationDeliveryResult result = await delivery.DeliverAsync(
                Recipient,
                SyntheticToken,
                timeout.Token);
            await serverTask;

            Assert.Equal(EmailVerificationDeliveryResult.Failed, result);
            AssertSanitizedFailure(logger);
        }
        finally
        {
            await timeout.CancelAsync();
            listener.Stop();
            await DrainServerTaskAsync(serverTask, timeout.Token);
        }
    }

    [Fact]
    public async Task DeliverAsync_UntrustedTlsCertificate_ReturnsFailedWithSanitizedTelemetry()
    {
        var logger = new CapturingLogger<MailKitEmailVerificationDelivery>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        Task serverTask = Task.CompletedTask;

        try
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            MailKitEmailVerificationDelivery delivery = CreateDelivery(
                CreateOptions(
                    port,
                    includeCredentials: false,
                    SecureSocketOptions.SslOnConnect),
                logger);
            serverTask = NegotiateWithUntrustedCertificateAsync(
                listener,
                timeout.Token);

            EmailVerificationDeliveryResult result = await delivery.DeliverAsync(
                Recipient,
                SyntheticToken,
                timeout.Token);
            await serverTask;

            Assert.Equal(EmailVerificationDeliveryResult.Failed, result);
            AssertSanitizedFailure(logger);
        }
        finally
        {
            await timeout.CancelAsync();
            listener.Stop();
            await DrainServerTaskAsync(serverTask, timeout.Token);
        }
    }

    [Fact]
    public async Task DeliverAsync_CancelledToken_PropagatesCancellation()
    {
        var logger = new CapturingLogger<MailKitEmailVerificationDelivery>();
        MailKitEmailVerificationDelivery delivery = CreateDelivery(
            CreateOptions(1, includeCredentials: true),
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

    private static async Task AcceptAndTerminateConnectionAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using TcpClient acceptedClient = await listener.AcceptTcpClientAsync(
            cancellationToken);
    }

    private static async Task RejectAuthenticationAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using TcpClient acceptedClient = await listener.AcceptTcpClientAsync(
            cancellationToken);
        await using NetworkStream stream = acceptedClient.GetStream();
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        await using var writer = new StreamWriter(
            stream,
            Encoding.ASCII,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n"
        };

        await writer.WriteLineAsync("220 localhost ESMTP");
        string? ehlo = await reader.ReadLineAsync(cancellationToken);
        Assert.NotNull(ehlo);
        Assert.StartsWith("EHLO ", ehlo, StringComparison.Ordinal);
        await writer.WriteLineAsync("250-localhost");
        await writer.WriteLineAsync("250 AUTH PLAIN");
        string? authentication = await reader.ReadLineAsync(cancellationToken);
        Assert.NotNull(authentication);
        Assert.StartsWith("AUTH PLAIN ", authentication, StringComparison.Ordinal);
        await writer.WriteLineAsync("535 5.7.8 Authentication credentials invalid");
    }

    private static async Task NegotiateWithUntrustedCertificateAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using RSA rsa = RSA.Create(2_048);
        var certificateRequest = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = certificateRequest.CreateSelfSigned(
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using TcpClient acceptedClient = await listener.AcceptTcpClientAsync(
            cancellationToken);
        await using var sslStream = new SslStream(acceptedClient.GetStream());

        try
        {
            await sslStream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.None,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                },
                cancellationToken);
        }
        catch (System.Security.Authentication.AuthenticationException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task DrainServerTaskAsync(
        Task serverTask,
        CancellationToken cancellationToken)
    {
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal static EmailVerificationDeliveryOptions CreateOptions(
        int smtpPort,
        bool includeCredentials,
        SecureSocketOptions smtpSecurity = SecureSocketOptions.None)
    {
        return new EmailVerificationDeliveryOptions
        {
            VerificationPageUrl = "https://app.example/verify-email",
            SenderName = "ENMA",
            SenderAddress = "no-reply@example.test",
            SmtpHost = "127.0.0.1",
            SmtpPort = smtpPort,
            SmtpSecurity = smtpSecurity,
            SmtpUsername = includeCredentials ? "smtp-user" : string.Empty,
            SmtpPassword = includeCredentials ? SyntheticPassword : string.Empty
        };
    }

    internal static MailKitEmailVerificationDelivery CreateDelivery(
        EmailVerificationDeliveryOptions options,
        CapturingLogger<MailKitEmailVerificationDelivery> logger)
    {
        IOptions<EmailVerificationDeliveryOptions> wrappedOptions = Options.Create(options);
        var linkBuilder = new EmailVerificationLinkBuilder(wrappedOptions);

        return new MailKitEmailVerificationDelivery(
            wrappedOptions,
            linkBuilder,
            logger);
    }

    internal static void AssertNoSensitiveTelemetry(IReadOnlyList<LogEntry> entries)
    {
        string verificationUrl =
            $"https://app.example/verify-email#token={SyntheticToken}";
        string[] sensitiveValues =
        [
            Recipient,
            SyntheticToken,
            verificationUrl,
            "smtp-user",
            SyntheticPassword
        ];

        foreach (string sensitiveValue in sensitiveValues)
        {
            Assert.DoesNotContain(
                entries,
                entry => entry.Message.Contains(
                    sensitiveValue,
                    StringComparison.OrdinalIgnoreCase)
                    || (entry.Exception?.ToString().Contains(
                        sensitiveValue,
                        StringComparison.OrdinalIgnoreCase) ?? false));
        }
    }

    private static void AssertSanitizedFailure(
        CapturingLogger<MailKitEmailVerificationDelivery> logger)
    {
        LogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(2001, entry.EventId.Id);
        Assert.Null(entry.Exception);
        AssertNoSensitiveTelemetry(logger.Entries);
    }

    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (entries)
                {
                    return entries.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (entries)
            {
                entries.Add(new LogEntry(
                    logLevel,
                    eventId,
                    formatter(state, exception),
                    exception));
            }
        }
    }

    internal sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
