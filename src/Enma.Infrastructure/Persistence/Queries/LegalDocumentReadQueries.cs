using Enma.Application.Documents;
using Enma.Domain.Documents;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class LegalDocumentReadQueries : ILegalDocumentReadQueries
{
    private const string LikeEscapeCharacter = "\\";

    private readonly EnmaDbContext _dbContext;

    public LegalDocumentReadQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<LegalDocumentMetadataReadModel?> FindAsync(
        Guid documentId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.LegalDocuments
            .AsNoTracking()
            .Where(document =>
                document.Id == documentId &&
                document.OrganizationId == organizationId)
            .Select(document => new LegalDocumentMetadataReadModel(
                document.Id,
                document.ClientId,
                document.ProcessId,
                document.OriginalFileName,
                document.ContentType,
                document.SizeBytes,
                document.ContentHashSha256,
                document.UploadedByMembershipId,
                document.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<LegalDocumentListReadPage> ListAsync(
        LegalDocumentListReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        int skippedItems = checked(
            (request.PageNumber - 1) * request.PageSize);
        IQueryable<LegalDocument> documents = _dbContext.LegalDocuments
            .AsNoTracking()
            .Where(document =>
                document.OrganizationId == request.OrganizationId);

        if (request.FileNameSearch is not null)
        {
            string pattern =
                $"%{EscapeLikePattern(request.FileNameSearch)}%";
            documents = documents.Where(document =>
                EF.Functions.ILike(
                    document.OriginalFileName,
                    pattern,
                    LikeEscapeCharacter));
        }

        if (request.ProcessId is Guid processId)
        {
            documents = documents.Where(document =>
                document.ProcessId == processId);
        }

        if (request.ClientId is Guid clientId)
        {
            documents = documents.Where(document =>
                document.ClientId == clientId ||
                document.ProcessId.HasValue &&
                _dbContext.LegalProcesses.Any(legalProcess =>
                    legalProcess.OrganizationId == request.OrganizationId &&
                    legalProcess.Id == document.ProcessId.Value &&
                    legalProcess.ClientId == clientId));
        }

        LegalDocumentMetadataReadModel[] items = await documents
            .OrderByDescending(document => document.CreatedAt)
            .ThenByDescending(document => document.Id)
            .Skip(skippedItems)
            .Take(request.PageSize + 1)
            .Select(document => new LegalDocumentMetadataReadModel(
                document.Id,
                document.ClientId,
                document.ProcessId,
                document.OriginalFileName,
                document.ContentType,
                document.SizeBytes,
                document.ContentHashSha256,
                document.UploadedByMembershipId,
                document.CreatedAt))
            .ToArrayAsync(cancellationToken);
        bool hasNext = items.Length > request.PageSize;

        return new LegalDocumentListReadPage(
            hasNext ? items[..request.PageSize] : items,
            hasNext);
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
