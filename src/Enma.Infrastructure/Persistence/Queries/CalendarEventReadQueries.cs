using Enma.Application.CalendarEvents;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class CalendarEventReadQueries : ICalendarEventReadQueries
{
    private readonly EnmaDbContext _dbContext;

    public CalendarEventReadQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<CalendarEventDetailReadModel?> FindAsync(
        Guid calendarEventId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<CalendarEventDetailReadModel> query =
            from calendarEvent in _dbContext.CalendarEvents.AsNoTracking()
            join client in _dbContext.Clients.AsNoTracking()
                on new
                {
                    calendarEvent.OrganizationId,
                    ClientId = calendarEvent.ClientId
                }
                equals new
                {
                    client.OrganizationId,
                    ClientId = (Guid?)client.Id
                }
                into clients
            from client in clients.DefaultIfEmpty()
            join legalProcess in _dbContext.LegalProcesses.AsNoTracking()
                on new
                {
                    calendarEvent.OrganizationId,
                    ProcessId = calendarEvent.ProcessId
                }
                equals new
                {
                    legalProcess.OrganizationId,
                    ProcessId = (Guid?)legalProcess.Id
                }
                into legalProcesses
            from legalProcess in legalProcesses.DefaultIfEmpty()
            join creatorMembership in
                _dbContext.OrganizationMemberships.AsNoTracking()
                on new
                {
                    calendarEvent.OrganizationId,
                    MembershipId = calendarEvent.CreatedByMembershipId
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
                    calendarEvent.OrganizationId,
                    MembershipId = calendarEvent.AssigneeMembershipId
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
            where calendarEvent.Id == calendarEventId &&
                calendarEvent.OrganizationId == organizationId
            select new CalendarEventDetailReadModel(
                calendarEvent.Id,
                calendarEvent.Title,
                calendarEvent.Description,
                calendarEvent.StartsAt,
                calendarEvent.EndsAt,
                calendarEvent.Location,
                calendarEvent.ClientId,
                client == null ? null : client.Name,
                calendarEvent.ProcessId,
                legalProcess == null ? null : legalProcess.Title,
                calendarEvent.AssigneeMembershipId,
                assigneeUser == null ? null : assigneeUser.Name,
                calendarEvent.CreatedByMembershipId,
                creatorUser.Name,
                calendarEvent.CreatedAt);

        return query.SingleOrDefaultAsync(cancellationToken);
    }
}
