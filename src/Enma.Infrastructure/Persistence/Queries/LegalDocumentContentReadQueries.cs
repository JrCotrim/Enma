using Enma.Application.Documents.Download;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class LegalDocumentContentReadQueries
    : ILegalDocumentContentReadQueries
{
    private readonly EnmaDbContext dbContext;

    public LegalDocumentContentReadQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        this.dbContext = dbContext;
    }

    public Task<LegalDocumentContentReadModel?> FindAsync(
        Guid organizationId,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.LegalDocuments
            .AsNoTracking()
            .Where(document =>
                document.OrganizationId == organizationId &&
                document.Id == documentId)
            .Select(document => new LegalDocumentContentReadModel(
                document.Id,
                document.OriginalFileName,
                document.ContentType,
                document.SizeBytes,
                document.StoredObjectKey))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
