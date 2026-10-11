using System.Net;
using System.Net.Sockets;
using Enma.Api.Documents;
using Enma.Api.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Enma.IntegrationTests.Api;

// API hosts built by integration tests must not run the periodic workers
// against whatever database their connection string names, and must not hand
// mail to the developer's Mailpit on the default Development port 1025.
internal static class TestHostIsolation
{
    public const string DevelopmentSmtpPortKey =
        "EmailVerification:DevelopmentDelivery:SmtpPort";

    private static readonly Type[] BackgroundWorkerTypes =
    [
        typeof(NotificationGenerationWorker),
        typeof(LegalDocumentDeletionWorker)
    ];

    // A loopback port that was free when the test run started and has no
    // listener; Development delivery against it fails fast instead of
    // reaching a real mail sink.
    public static int UnusedSmtpPort { get; } = ReserveUnusedLoopbackPort();

    public static void RemoveBackgroundWorkers(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        ServiceDescriptor[] workers = services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                BackgroundWorkerTypes.Contains(descriptor.ImplementationType))
            .ToArray();
        foreach (ServiceDescriptor worker in workers)
        {
            services.Remove(worker);
        }
    }

    private static int ReserveUnusedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
