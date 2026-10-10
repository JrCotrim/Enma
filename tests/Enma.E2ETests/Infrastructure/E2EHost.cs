using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Playwright;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Enma.E2ETests.Infrastructure;

// One API host per journey class, on top of the shared E2EStack: the API with
// the Enma.Web build on its own HTTPS origin. Rate limits stay real and live in
// the host's memory, so each class starts with a fresh login/invitation budget
// instead of sharing one budget across the whole run.
public sealed class E2EHost : IAsyncLifetime
{
    private readonly IMessageSink diagnostics;

    private readonly string dataProtectionKeysPath = Path.Combine(
        Path.GetTempPath(),
        $"enma-e2e-keys-{Guid.NewGuid():N}");

    private EnmaE2EApplicationFactory? application;
    private Uri? baseAddress;

    public E2EHost(E2EStack stack, IMessageSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(diagnostics);
        Stack = stack;
        this.diagnostics = diagnostics;
    }

    public E2EStack Stack { get; }

    public string RepositoryRoot => Stack.RepositoryRoot;

    public IBrowser Browser => Stack.Browser;

    public MailpitClient Mailpit => Stack.Mailpit;

    public Uri BaseAddress => baseAddress ?? throw new InvalidOperationException(
        "The E2E host has not started.");

    public ApiSeeder Seeder => new(BaseAddress, Mailpit);

    public Task InitializeAsync()
    {
        var startup = Stopwatch.StartNew();
        int port = FindFreeLoopbackPort();
        baseAddress = new Uri($"https://localhost:{port}/");
        Directory.CreateDirectory(dataProtectionKeysPath);
        application = new EnmaE2EApplicationFactory(
            Stack.CreateApplicationSettings(baseAddress),
            Stack.DistributionPath,
            dataProtectionKeysPath);
        application.UseKestrel(options =>
            options.ListenLocalhost(port, listen => listen.UseHttps()));
        application.StartServer();

        // Visible with `dotnet test tests/Enma.E2ETests -- xUnit.DiagnosticMessages=true`.
        diagnostics.OnMessage(new DiagnosticMessage(
            $"E2E host started in {startup.ElapsedMilliseconds} ms."));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (application is not null)
        {
            await application.DisposeAsync();
        }

        if (Directory.Exists(dataProtectionKeysPath))
        {
            Directory.Delete(dataProtectionKeysPath, recursive: true);
        }
    }

    private static int FindFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
