namespace Enma.Api.Contracts.Documents;

public sealed class UploadLegalDocumentRequest
{
    public IFormFile? File { get; set; }

    public Guid? ClientId { get; set; }

    public Guid? ProcessId { get; set; }
}
