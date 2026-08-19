namespace Enma.Application.Documents.Upload;

public sealed class UploadLegalDocumentCommand
{
    public UploadLegalDocumentCommand(
        Guid userId,
        Guid organizationId,
        Guid? clientId,
        Guid? processId,
        string? originalFileName,
        string? submittedContentType,
        long declaredContentLength,
        Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);

        UserId = userId;
        OrganizationId = organizationId;
        ClientId = clientId;
        ProcessId = processId;
        OriginalFileName = originalFileName;
        SubmittedContentType = submittedContentType;
        DeclaredContentLength = declaredContentLength;
        Content = content;
    }

    public Guid UserId { get; }

    public Guid OrganizationId { get; }

    public Guid? ClientId { get; }

    public Guid? ProcessId { get; }

    public string? OriginalFileName { get; }

    public string? SubmittedContentType { get; }

    public long DeclaredContentLength { get; }

    public Stream Content { get; }
}
