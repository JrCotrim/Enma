namespace Enma.Application.Documents.Staging;

public interface ILegalDocumentContentStager
{
    Task<ILegalDocumentStagedContent> StageAsync(
        Stream source,
        long declaredContentLength,
        CancellationToken cancellationToken = default);
}
