using Enma.Application.Deadlines;
using Enma.Domain.Deadlines;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class LegalDeadlineReadQueries : ILegalDeadlineReadQueries
{
    private readonly EnmaDbContext _dbContext;

    public LegalDeadlineReadQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<LegalDeadlineDetailReadModel?> FindAsync(
        Guid deadlineId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<LegalDeadlineDetailReadModel> query =
            from legalDeadline in _dbContext.LegalDeadlines.AsNoTracking()
            join legalProcess in _dbContext.LegalProcesses.AsNoTracking()
                on new
                {
                    legalDeadline.OrganizationId,
                    ProcessId = legalDeadline.ProcessId
                }
                equals new
                {
                    legalProcess.OrganizationId,
                    ProcessId = legalProcess.Id
                }
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
                    legalDeadline.OrganizationId,
                    MembershipId = legalDeadline.ResponsibleMembershipId
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
            where legalDeadline.Id == deadlineId &&
                legalDeadline.OrganizationId == organizationId
            select new LegalDeadlineDetailReadModel(
                legalDeadline.Id,
                legalDeadline.Title,
                legalDeadline.DueDate,
                legalDeadline.ProcessId,
                legalProcess.Title,
                client.Name,
                legalDeadline.CompletedAt == null
                    ? LegalDeadlineReadState.Pending
                    : LegalDeadlineReadState.Completed,
                legalDeadline.CreatedAt,
                legalDeadline.CompletedAt,
                legalDeadline.ResponsibleMembershipId,
                responsibleUser == null ? null : responsibleUser.Name);

        return query.SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LegalDeadlineListItem>> ListAsync(
        LegalDeadlineListReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        int skippedItems = checked((request.PageNumber - 1) * request.PageSize);

        IQueryable<LegalDeadline> legalDeadlines = _dbContext.LegalDeadlines
            .AsNoTracking()
            .Where(legalDeadline =>
                legalDeadline.OrganizationId == request.OrganizationId);

        legalDeadlines = request.ResponsibleFilterKind switch
        {
            LegalDeadlineReadResponsibleFilterKind.Any => legalDeadlines,
            LegalDeadlineReadResponsibleFilterKind.Unassigned =>
                legalDeadlines.Where(legalDeadline =>
                    legalDeadline.ResponsibleMembershipId == null),
            LegalDeadlineReadResponsibleFilterKind.Membership
                when request.ResponsibleMembershipId is Guid membershipId =>
                legalDeadlines.Where(legalDeadline =>
                    legalDeadline.ResponsibleMembershipId == membershipId),
            _ => throw new ArgumentException(
                "The legal deadline responsible filter is invalid.",
                nameof(request))
        };

        IQueryable<LegalDeadlineListItem> query =
            from legalDeadline in legalDeadlines
            join legalProcess in _dbContext.LegalProcesses.AsNoTracking()
                on new
                {
                    legalDeadline.OrganizationId,
                    ProcessId = legalDeadline.ProcessId
                }
                equals new
                {
                    legalProcess.OrganizationId,
                    ProcessId = legalProcess.Id
                }
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
                    legalDeadline.OrganizationId,
                    MembershipId = legalDeadline.ResponsibleMembershipId
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
            orderby legalDeadline.DueDate, legalDeadline.Id
            select new LegalDeadlineListItem(
                legalDeadline.Id,
                legalDeadline.Title,
                legalDeadline.DueDate,
                legalDeadline.ProcessId,
                legalProcess.Title,
                client.Name,
                legalDeadline.CompletedAt == null
                    ? LegalDeadlineReadState.Pending
                    : LegalDeadlineReadState.Completed,
                legalDeadline.ResponsibleMembershipId,
                responsibleUser == null ? null : responsibleUser.Name);

        return await query
            .Skip(skippedItems)
            .Take(request.PageSize)
            .ToArrayAsync(cancellationToken);
    }
}
