namespace Enma.Infrastructure.Documents.Storage;

public sealed class DocumentStorageOptions
{
    public const string SectionName = "DocumentStorage";

    public string ServiceUrl { get; init; } = string.Empty;

    public string BucketName { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;

    public bool ForcePathStyle { get; init; }

    public string AccessKey { get; init; } = string.Empty;

    public string SecretKey { get; init; } = string.Empty;

    public bool RequireTls { get; init; } = true;

    // Opt-in for plain HTTP to a single-label host (no dot, not an IP), such
    // as the Compose service name of a co-located MinIO on an internal
    // network. Off by default; RequireTls must still be disabled.
    public bool AllowPlainHttpSingleLabelHost { get; init; }
}
