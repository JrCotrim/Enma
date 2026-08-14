using Enma.Application.Tasks;
using Enma.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class LegalTaskReadQueries : ILegalTaskReadQueries
{
    private readonly EnmaDbContext _dbContext;

    public LegalTaskReadQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<LegalTaskDetailReadModel?> FindAsync(
        Guid legalTaskId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<LegalTaskDetailReadModel> query =
            from legalTask in _dbContext.LegalTasks.AsNoTracking()
            join legalProcess in _dbContext.LegalProcesses.AsNoTracking()
                on new
                {
                    legalTask.OrganizationId,
                    ProcessId = legalTask.ProcessId
                }
                equals new
                {
                    legalProcess.OrganizationId,
                    ProcessId = (Guid?)legalProcess.Id
                }
                into legalProcesses
            from legalProcess in legalProcesses.DefaultIfEmpty()
            join client in _dbContext.Clients.AsNoTracking()
                on new
                {
                    legalTask.OrganizationId,
                    ClientId = (Guid?)legalProcess.ClientId
                }
                equals new
                {
                    client.OrganizationId,
                    ClientId = (Guid?)client.Id
                }
                into clients
            from client in clients.DefaultIfEmpty()
            join creatorMembership in
                _dbContext.OrganizationMemberships.AsNoTracking()
                on new
                {
                    legalTask.OrganizationId,
                    MembershipId = legalTask.CreatedByMembershipId
                }
                equals new
                {
                    creatorMembership.OrganizationId,
                    MembershipId = creatorMembership.Id
                }
            join creatorUser in _dbContext.Users.AsNoTracking()
                on creatorMembership.UserId equals creatorUser.Id
            join assigneeMembership in
                _dbContext.OrganizationMemberships.AsNoTracking()
                on new
                {
                    legalTask.OrganizationId,
                    MembershipId = legalTask.AssigneeMembershipId
                }
                equals new
                {
                    assigneeMembership.OrganizationId,
                    MembershipId = (Guid?)assigneeMembership.Id
                }
                into assigneeMemberships
            from assigneeMembership in assigneeMemberships.DefaultIfEmpty()
            join assigneeUser in _dbContext.Users.AsNoTracking()
                on assigneeMembership.UserId equals assigneeUser.Id
                into assigneeUsers
            from assigneeUser in assigneeUsers.DefaultIfEmpty()
            where legalTask.Id == legalTaskId &&
                legalTask.OrganizationId == organizationId
            select new LegalTaskDetailReadModel(
                legalTask.Id,
                legalTask.Title,
                legalTask.Description,
                legalTask.DueDate,
                legalTask.ProcessId,
                legalProcess == null ? null : legalProcess.Title,
                client == null ? null : client.Name,
                legalTask.AssigneeMembershipId,
                assigneeUser == null ? null : assigneeUser.Name,
                legalTask.CreatedByMembershipId,
                creatorUser.Name,
                legalTask.CompletedAt == null
                    ? LegalTaskState.Pending
                    : LegalTaskState.Completed,
                legalTask.CreatedAt,
                legalTask.CompletedAt);

        return query.SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<LegalTaskListReadPage> ListAsync(
        LegalTaskListReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        int skippedItems = checked((request.PageNumber - 1) * request.PageSize);
        IQueryable<LegalTask> legalTasks = _dbContext.LegalTasks
            .AsNoTracking()
            .Where(legalTask =>
                legalTask.OrganizationId == request.OrganizationId);

        legalTasks = request.State switch
        {
            LegalTaskState.Pending => legalTasks.Where(
                legalTask => legalTask.CompletedAt == null),
            LegalTaskState.Completed => legalTasks.Where(
                legalTask => legalTask.CompletedAt != null),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };

        if (request.ProcessId is Guid processId)
        {
            legalTasks = legalTasks.Where(
                legalTask => legalTask.ProcessId == processId);
        }

        legalTasks = request.AssigneeFilterKind switch
        {
            LegalTaskReadAssigneeFilterKind.Any => legalTasks,
            LegalTaskReadAssigneeFilterKind.Unassigned => legalTasks.Where(
                legalTask => legalTask.AssigneeMembershipId == null),
            LegalTaskReadAssigneeFilterKind.Membership => legalTasks.Where(
                legalTask => legalTask.AssigneeMembershipId ==
                    request.AssigneeMembershipId),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };

        IOrderedQueryable<LegalTask> orderedLegalTasks = request.State switch
        {
            LegalTaskState.Pending => legalTasks
                .OrderBy(legalTask => legalTask.DueDate)
                .ThenByDescending(legalTask => legalTask.CreatedAt)
                .ThenBy(legalTask => legalTask.Id),
            LegalTaskState.Completed => legalTasks
                .OrderByDescending(legalTask => legalTask.CompletedAt)
                .ThenBy(legalTask => legalTask.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };

        IQueryable<LegalTaskListItem> query =
            from legalTask in orderedLegalTasks
            join legalProcess in _dbContext.LegalProcesses.AsNoTracking()
                on new
                {
                    legalTask.OrganizationId,
                    ProcessId = legalTask.ProcessId
                }
                equals new
                {
                    legalProcess.OrganizationId,
                    ProcessId = (Guid?)legalProcess.Id
                }
                into legalProcesses
            from legalProcess in legalProcesses.DefaultIfEmpty()
            join client in _dbContext.Clients.AsNoTracking()
                on new
                {
                    legalTask.OrganizationId,
                    ClientId = (Guid?)legalProcess.ClientId
                }
                equals new
                {
                    client.OrganizationId,
                    ClientId = (Guid?)client.Id
                }
                into clients
            from client in clients.DefaultIfEmpty()
            join assigneeMembership in
                _dbContext.OrganizationMemberships.AsNoTracking()
                on new
                {
                    legalTask.OrganizationId,
                    MembershipId = legalTask.AssigneeMembershipId
                }
                equals new
                {
                    assigneeMembership.OrganizationId,
                    MembershipId = (Guid?)assigneeMembership.Id
                }
                into assigneeMemberships
            from assigneeMembership in assigneeMemberships.DefaultIfEmpty()
            join assigneeUser in _dbContext.Users.AsNoTracking()
                on assigneeMembership.UserId equals assigneeUser.Id
                into assigneeUsers
            from assigneeUser in assigneeUsers.DefaultIfEmpty()
            select new LegalTaskListItem(
                legalTask.Id,
                legalTask.Title,
                legalTask.DueDate,
                legalTask.ProcessId,
                legalProcess == null ? null : legalProcess.Title,
                client == null ? null : client.Name,
                legalTask.AssigneeMembershipId,
                assigneeUser == null ? null : assigneeUser.Name,
                legalTask.CreatedByMembershipId,
                legalTask.CompletedAt == null
                    ? LegalTaskState.Pending
                    : LegalTaskState.Completed,
                legalTask.CreatedAt);

        LegalTaskListItem[] items = await query
            .Skip(skippedItems)
            .Take(request.PageSize + 1)
            .ToArrayAsync(cancellationToken);
        bool hasNext = items.Length > request.PageSize;

        return new LegalTaskListReadPage(
            hasNext ? items[..request.PageSize] : items,
            hasNext);
    }
}
