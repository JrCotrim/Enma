namespace Enma.Api.Contracts.Documents;

public sealed record LegalDocumentMetadataResponse(
    Guid Id,
    Guid? ClientId,
    Guid? ProcessId,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset CreatedAt);
