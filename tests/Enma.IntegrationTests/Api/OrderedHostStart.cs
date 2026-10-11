using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Enma.IntegrationTests.Api;

// WebApplicationFactory lets Program run app.Run() on its own thread while the
// test thread starts a deferred host that first subscribes to the real host's
// lifetime. When startup validation fails, app.Run() disposes the host; if
// that wins the race, the subscription throws ObjectDisposedException and the
// real startup failure is lost. The application host here waits in its
// lifetime, before any startup validation runs, until the subscription is in
// place, so a failed start always surfaces the application's own exception.
internal static class OrderedHostStart
{
    public static IHost Start(IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var lifetime = new GatedHostLifetime();
        builder.ConfigureServices(services =>
            services.AddSingleton<IHostLifetime>(lifetime));

        IHost host = builder.Build();
        Task start;
        try
        {
            // The deferred host subscribes synchronously, before its first await.
            start = host.StartAsync();
        }
        finally
        {
            lifetime.Open();
        }

        start.GetAwaiter().GetResult();
        return host;
    }

    private sealed class GatedHostLifetime : IHostLifetime
    {
        private readonly TaskCompletionSource gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open()
        {
            gate.TrySetResult();
        }

        public Task WaitForStartAsync(CancellationToken cancellationToken)
        {
            return gate.Task.WaitAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
