namespace Enma.Api.Deployment;

internal sealed class TrustedProxyOptions
{
    public const string SectionName = "Deployment:TrustedProxy";

    public bool Enabled { get; set; }

    public string[] KnownProxies { get; set; } = [];

    public string[] KnownIPNetworks { get; set; } = [];
}
