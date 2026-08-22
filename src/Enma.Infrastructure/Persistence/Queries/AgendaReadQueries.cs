using Enma.Application.Agenda;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class AgendaReadQueries : IAgendaReadQueries
{
    private readonly EnmaDbContext _dbContext;

    public AgendaReadQueries(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<AgendaItemReadModel>> ReadAsync(
        AgendaReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        AgendaItemReadModel[] deadlines = await ReadDeadlinesAsync(
            request,
            cancellationToken);
        AgendaItemReadModel[] tasks = await ReadTasksAsync(
            request,
            cancellationToken);
        AgendaItemReadModel[] calendarEvents = await ReadCalendarEventsAsync(
            request,
            cancellationToken);

        return deadlines
            .Concat(tasks)
            .Concat(calendarEvents)
            .ToArray();
    }

    private Task<AgendaItemReadModel[]> ReadDeadlinesAsync(
        AgendaReadRequest request,
        CancellationToken cancellationToken)
    {
        IQueryable<AgendaItemReadModel> query =
            from deadline in _dbContext.LegalDeadlines.AsNoTracking()
            join legalProcess in _dbContext.LegalProcesses.AsNoTracking()
                on new
                {
                    deadline.OrganizationId,
                    ProcessId = deadline.ProcessId
                }
                equals new
                {
                    legalProcess.OrganizationId,
                    ProcessId = legalProcess.Id
                }
            join client in _dbContext.Clients.AsNoTracking()
                on new
                {
                    deadline.OrganizationId,
                    ClientId = legalProcess.ClientId
                }
                equals new
                {
                    client.OrganizationId,
                    ClientId = client.Id
                }
            where deadline.OrganizationId == request.OrganizationId &&
                deadline.DueDate >= request.LocalStartDate &&
                deadline.DueDate < request.LocalEndDate
            orderby deadline.DueDate, deadline.Id
            select new AgendaItemReadModel(
                AgendaItemKind.Deadline,
                deadline.Id,
                deadline.Title,
                true,
                deadline.DueDate,
                null,
                null,
                deadline.CompletedAt,
                client.Id,
                client.Name,
                legalProcess.Id,
                legalProcess.Title,
                null,
                null);

        return query.ToArrayAsync(cancellationToken);
    }

    private Task<AgendaItemReadModel[]> ReadTasksAsync(
        AgendaReadRequest request,
        CancellationToken cancellationToken)
    {
        IQueryable<AgendaItemReadModel> query =
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
            where legalTask.OrganizationId == request.OrganizationId &&
                legalTask.DueDate != null &&
                legalTask.DueDate >= request.LocalStartDate &&
                legalTask.DueDate < request.LocalEndDate
            orderby legalTask.DueDate, legalTask.Id
            select new AgendaItemReadModel(
                AgendaItemKind.Task,
                legalTask.Id,
                legalTask.Title,
                true,
                legalTask.DueDate,
                null,
                null,
                legalTask.CompletedAt,
                client == null ? null : client.Id,
                client == null ? null : client.Name,
                legalProcess == null ? null : legalProcess.Id,
                legalProcess == null ? null : legalProcess.Title,
                legalTask.AssigneeMembershipId,
                assigneeUser == null ? null : assigneeUser.Name);

        return query.ToArrayAsync(cancellationToken);
    }

    private Task<AgendaItemReadModel[]> ReadCalendarEventsAsync(
        AgendaReadRequest request,
        CancellationToken cancellationToken)
    {
        IQueryable<AgendaItemReadModel> query =
            from calendarEvent in _dbContext.CalendarEvents.AsNoTracking()
            join directClient in _dbContext.Clients.AsNoTracking()
                on new
                {
                    calendarEvent.OrganizationId,
                    ClientId = calendarEvent.ClientId
                }
                equals new
                {
                    directClient.OrganizationId,
                    ClientId = (Guid?)directClient.Id
                }
                into directClients
            from directClient in directClients.DefaultIfEmpty()
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
            join processClient in _dbContext.Clients.AsNoTracking()
                on new
                {
                    calendarEvent.OrganizationId,
                    ClientId = (Guid?)legalProcess.ClientId
                }
                equals new
                {
                    processClient.OrganizationId,
                    ClientId = (Guid?)processClient.Id
                }
                into processClients
            from processClient in processClients.DefaultIfEmpty()
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
            where calendarEvent.OrganizationId == request.OrganizationId &&
                calendarEvent.StartsAt < request.ToUtc &&
                calendarEvent.EndsAt > request.FromUtc
            orderby calendarEvent.StartsAt, calendarEvent.EndsAt, calendarEvent.Id
            select new AgendaItemReadModel(
                AgendaItemKind.CalendarEvent,
                calendarEvent.Id,
                calendarEvent.Title,
                false,
                null,
                calendarEvent.StartsAt,
                calendarEvent.EndsAt,
                null,
                calendarEvent.ClientId ??
                    (legalProcess == null ? null : legalProcess.ClientId),
                directClient == null
                    ? processClient == null ? null : processClient.Name
                    : directClient.Name,
                calendarEvent.ProcessId,
                legalProcess == null ? null : legalProcess.Title,
                calendarEvent.AssigneeMembershipId,
                assigneeUser == null ? null : assigneeUser.Name);

        return query.ToArrayAsync(cancellationToken);
    }
}
