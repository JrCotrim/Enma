using Docker.DotNet.Models;

namespace Enma.IntegrationTests.Infrastructure;

// Testcontainers publishes ports on every host interface by default. Test
// containers carry synthetic credentials but must still not be reachable from
// the network, so each published port is pinned to the IPv4 loopback.
internal static class LoopbackPortBindings
{
    public const string HostIp = "127.0.0.1";

    public static void Apply(CreateContainerParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        foreach (IList<PortBinding> bindings in
            parameters.HostConfig?.PortBindings?.Values ?? [])
        {
            foreach (PortBinding binding in bindings)
            {
                binding.HostIP = HostIp;
            }
        }
    }
}
