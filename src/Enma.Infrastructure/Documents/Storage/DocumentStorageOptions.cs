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
}
