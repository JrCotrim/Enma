using Enma.Application.Processes;
using Enma.Domain.Processes;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class LegalProcessReadQueries : ILegalProcessReadQueries
{
    private const string LikeEscapeCharacter =
        LegalProcessSearchPattern.LikeEscapeCharacter;

    private readonly EnmaDbContext _dbContext;

    public LegalProcessReadQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<LegalProcessReadModel?> FindAsync(
        Guid processId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<LegalProcessReadModel> query =
            from legalProcess in _dbContext.LegalProcesses.AsNoTracking()
            join client in _dbContext.Clients.AsNoTracking()
                on new
                {
                    legalProcess.OrganizationId,
                    ClientId = legalProcess.ClientId
                }
                equals new
                {
                    client.OrganizationId,
                    ClientId = client.Id
                }
            join responsibleMembership in
                _dbContext.OrganizationMemberships.AsNoTracking()
                on new
                {
                    legalProcess.OrganizationId,
                    MembershipId = legalProcess.ResponsibleMembershipId
                }
                equals new
                {
                    responsibleMembership.OrganizationId,
                    MembershipId = (Guid?)responsibleMembership.Id
                }
                into responsibleMemberships
            from responsibleMembership in responsibleMemberships.DefaultIfEmpty()
            join responsibleUser in _dbContext.Users.AsNoTracking()
                on responsibleMembership.UserId equals responsibleUser.Id
                into responsibleUsers
            from responsibleUser in responsibleUsers.DefaultIfEmpty()
            where legalProcess.Id == processId &&
                legalProcess.OrganizationId == organizationId
            select new LegalProcessReadModel(
                legalProcess.Id,
                legalProcess.Title,
                legalProcess.ClientId,
                client.Name,
                legalProcess.CreatedAt,
                legalProcess.ProcessNumber,
                legalProcess.Status,
                legalProcess.CourtOrAuthority,
                legalProcess.ResponsibleMembershipId,
                responsibleUser == null ? null : responsibleUser.Name);

        return query.SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LegalProcessReadModel>> ListAsync(
        LegalProcessListReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        long skippedItems = ((long)request.PageNumber - 1) * request.PageSize;

        if (skippedItems > int.MaxValue)
        {
            return Array.Empty<LegalProcessReadModel>();
        }

        IQueryable<LegalProcess> legalProcesses = _dbContext.LegalProcesses
            .AsNoTracking()
            .Where(legalProcess =>
                legalProcess.OrganizationId == request.OrganizationId);

        if (request.Status is LegalProcessStatus status)
        {
            legalProcesses = legalProcesses.Where(legalProcess =>
                legalProcess.Status == status);
        }

        legalProcesses = request.ResponsibleFilterKind switch
        {
            LegalProcessReadResponsibleFilterKind.Any => legalProcesses,
            LegalProcessReadResponsibleFilterKind.Unassigned =>
                legalProcesses.Where(legalProcess =>
                    legalProcess.ResponsibleMembershipId == null),
            LegalProcessReadResponsibleFilterKind.Membership
                when request.ResponsibleMembershipId is Guid membershipId =>
                legalProcesses.Where(legalProcess =>
                    legalProcess.ResponsibleMembershipId == membershipId),
            _ => throw new ArgumentException(
                "The legal process responsible filter is invalid.",
                nameof(request))
        };

        var query =
            from legalProcess in legalProcesses
            join client in _dbContext.Clients.AsNoTracking()
                on new
                {
                    legalProcess.OrganizationId,
                    ClientId = legalProcess.ClientId
                }
                equals new
                {
                    client.OrganizationId,
                    ClientId = client.Id
                }
            join responsibleMembership in
                _dbContext.OrganizationMemberships.AsNoTracking()
                on new
                {
                    legalProcess.OrganizationId,
                    MembershipId = legalProcess.ResponsibleMembershipId
                }
                equals new
                {
                    responsibleMembership.OrganizationId,
                    MembershipId = (Guid?)responsibleMembership.Id
                }
                into responsibleMemberships
            from responsibleMembership in responsibleMemberships.DefaultIfEmpty()
            join responsibleUser in _dbContext.Users.AsNoTracking()
                on responsibleMembership.UserId equals responsibleUser.Id
                into responsibleUsers
            from responsibleUser in responsibleUsers.DefaultIfEmpty()
            select new
            {
                LegalProcess = legalProcess,
                ClientName = client.Name,
                ResponsibleDisplayName =
                    responsibleUser == null ? null : responsibleUser.Name
            };

        if (LegalProcessSearchPattern.Create(request.Search) is { } searchPattern)
        {
            string pattern = searchPattern.ContainsPattern;
            string? digitsPattern = searchPattern.DigitsContainsPattern;
            query = query.Where(item =>
                EF.Functions.ILike(
                    item.LegalProcess.Title,
                    pattern,
                    LikeEscapeCharacter) ||
                EF.Functions.ILike(
                    item.ClientName,
                    pattern,
                    LikeEscapeCharacter) ||
                (item.LegalProcess.ProcessNumber != null &&
                    EF.Functions.ILike(
                        item.LegalProcess.ProcessNumber,
                        pattern,
                        LikeEscapeCharacter)) ||
                (digitsPattern != null &&
                    item.LegalProcess.NormalizedProcessNumber != null &&
                    EF.Functions.Like(
                        item.LegalProcess.NormalizedProcessNumber,
                        digitsPattern,
                        LikeEscapeCharacter)));
        }

        query = request.Sort switch
        {
            LegalProcessListSort.Title => query
                .OrderBy(item => item.LegalProcess.Title)
                .ThenBy(item => item.LegalProcess.Id),
            LegalProcessListSort.Newest => query
                .OrderByDescending(item => item.LegalProcess.CreatedAt)
                .ThenByDescending(item => item.LegalProcess.Id),
            _ => throw new ArgumentException(
                "The legal process list sort is invalid.",
                nameof(request))
        };

        return await query
            .Skip((int)skippedItems)
            .Take(request.PageSize + 1)
            .Select(item => new LegalProcessReadModel(
                item.LegalProcess.Id,
                item.LegalProcess.Title,
                item.LegalProcess.ClientId,
                item.ClientName,
                item.LegalProcess.CreatedAt,
                item.LegalProcess.ProcessNumber,
                item.LegalProcess.Status,
                item.LegalProcess.CourtOrAuthority,
                item.LegalProcess.ResponsibleMembershipId,
                item.ResponsibleDisplayName))
            .ToArrayAsync(cancellationToken);
    }
}
