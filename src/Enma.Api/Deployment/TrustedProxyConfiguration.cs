using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;

namespace Enma.Api.Deployment;

internal static class TrustedProxyConfiguration
{
    private const string ProductionValidationError =
        "Production ingress configuration is invalid.";
    private const string ValidationError =
        "Trusted proxy configuration is invalid.";

    private static readonly IPAddress IPv4MappedRangePrefix =
        IPAddress.Parse("::ffff:0:0");

    public static TrustedProxyTrustSet ValidateAndCreate(
        TrustedProxyOptions options,
        bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!TryParseProxies(options.KnownProxies, out IPAddress[] knownProxies) ||
            !TryParseNetworks(
                options.KnownIPNetworks,
                out System.Net.IPNetwork[] knownIPNetworks) ||
            (options.Enabled && knownProxies.Length + knownIPNetworks.Length == 0) ||
            (isProduction && !options.Enabled))
        {
            throw new InvalidOperationException(
                isProduction ? ProductionValidationError : ValidationError);
        }

        return new TrustedProxyTrustSet(
            options.Enabled,
            knownProxies,
            knownIPNetworks);
    }

    private static bool TryParseProxies(
        string[]? configuredProxies,
        out IPAddress[] knownProxies)
    {
        configuredProxies ??= [];
        knownProxies = new IPAddress[configuredProxies.Length];

        for (int index = 0; index < configuredProxies.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(configuredProxies[index]) ||
                !IPAddress.TryParse(configuredProxies[index], out IPAddress? proxy))
            {
                knownProxies = [];
                return false;
            }

            knownProxies[index] = proxy;
        }

        return true;
    }

    private static bool TryParseNetworks(
        string[]? configuredNetworks,
        out System.Net.IPNetwork[] knownIPNetworks)
    {
        configuredNetworks ??= [];
        knownIPNetworks = new System.Net.IPNetwork[configuredNetworks.Length];

        for (int index = 0; index < configuredNetworks.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(configuredNetworks[index]) ||
                !System.Net.IPNetwork.TryParse(
                    configuredNetworks[index],
                    out System.Net.IPNetwork network) ||
                network.PrefixLength == 0 ||
                ContainsEntireIPv4MappedRange(network))
            {
                knownIPNetworks = [];
                return false;
            }

            knownIPNetworks[index] = network;
        }

        return true;
    }

    private static bool ContainsEntireIPv4MappedRange(
        System.Net.IPNetwork network)
    {
        if (network.BaseAddress.AddressFamily != AddressFamily.InterNetworkV6 ||
            network.PrefixLength > 96)
        {
            return false;
        }

        Span<byte> networkBytes = stackalloc byte[16];
        Span<byte> mappedPrefixBytes = stackalloc byte[16];

        if (!network.BaseAddress.TryWriteBytes(
                networkBytes,
                out int networkBytesWritten) ||
            !IPv4MappedRangePrefix.TryWriteBytes(
                mappedPrefixBytes,
                out int mappedBytesWritten) ||
            networkBytesWritten != networkBytes.Length ||
            mappedBytesWritten != mappedPrefixBytes.Length)
        {
            return true;
        }

        int completeBytes = network.PrefixLength / 8;
        if (!networkBytes[..completeBytes]
            .SequenceEqual(mappedPrefixBytes[..completeBytes]))
        {
            return false;
        }

        int remainingBits = network.PrefixLength % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        byte mask = (byte)(byte.MaxValue << (8 - remainingBits));
        return (networkBytes[completeBytes] & mask) ==
            (mappedPrefixBytes[completeBytes] & mask);
    }
}

internal sealed class TrustedProxyTrustSet(
    bool enabled,
    IReadOnlyList<IPAddress> knownProxies,
    IReadOnlyList<System.Net.IPNetwork> knownIPNetworks)
{
    public bool Enabled { get; } = enabled;

    public void Configure(ForwardedHeadersOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;

        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (IPAddress knownProxy in knownProxies)
        {
            options.KnownProxies.Add(knownProxy);
        }

        foreach (System.Net.IPNetwork knownIPNetwork in knownIPNetworks)
        {
            options.KnownIPNetworks.Add(knownIPNetwork);
        }
    }
}
