namespace Enma.Application.Documents.Inspection;

public interface ILegalDocumentContentInspector
{
    Task InspectAsync(
        Stream content,
        long contentLength,
        LegalDocumentFileType fileType,
        CancellationToken cancellationToken = default);
}
