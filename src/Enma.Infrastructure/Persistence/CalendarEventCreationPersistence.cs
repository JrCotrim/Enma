using System.Data;
using Enma.Application.CalendarEvents;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class CalendarEventCreationPersistence
    : ICalendarEventCreationPersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;

    public CalendarEventCreationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        _dbContextOptions = dbContextOptions;
    }

    public async Task<CalendarEventCreationPersistenceResult> ExecuteAsync(
        CalendarEventCreationPersistenceRequest request,
        Func<CalendarEventCreationLockedState, CalendarEventCreationDecision> decide,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decide);

        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        Organization? organization = await LockOrganizationAsync(
            dbContext,
            request.OrganizationId,
            cancellationToken);
        CalendarEventLockedIdentities identities =
            await CalendarEventIdentityLocking.LockAsync(
                dbContext,
                request.OrganizationId,
                GetMembershipIds(request),
                cancellationToken);
        CalendarEventMemberState? actor =
            CalendarEventIdentityLocking.CreateMemberState(
                request.ActorMembershipId,
                identities);
        CalendarEventMemberState? assignee =
            request.AssigneeMembershipId is Guid assigneeMembershipId
                ? CalendarEventIdentityLocking.CreateMemberState(
                    assigneeMembershipId,
                    identities)
                : null;
        bool? isClientAvailable = request.ClientId is Guid clientId
            ? await LockActiveClientAsync(
                dbContext,
                request.OrganizationId,
                clientId,
                cancellationToken)
            : null;
        bool? isProcessAvailable = request.ProcessId is Guid processId
            ? await LockProcessAsync(
                dbContext,
                request.OrganizationId,
                processId,
                cancellationToken)
            : null;

        CalendarEventCreationDecision decision = decide(
            new CalendarEventCreationLockedState(
                organization?.IsActive == true,
                actor,
                assignee,
                isClientAvailable,
                isProcessAvailable));

        if (decision.Status != CalendarEventCreationDecisionStatus.Persist)
        {
            await transaction.RollbackAsync(cancellationToken);
            return CalendarEventCreationPersistenceResult.Rejected(decision.Status);
        }

        if (decision.CalendarEvent is not { } calendarEvent)
        {
            throw new InvalidOperationException(
                "A persistence decision must include a calendar event.");
        }

        await dbContext.CalendarEvents.AddAsync(calendarEvent, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return CalendarEventCreationPersistenceResult.Created(calendarEvent.Id);
    }

    private static IEnumerable<Guid> GetMembershipIds(
        CalendarEventCreationPersistenceRequest request)
    {
        return request.AssigneeMembershipId is Guid assigneeMembershipId
            ? new[] { request.ActorMembershipId, assigneeMembershipId }
                .Distinct()
                .OrderBy(membershipId => membershipId)
                .ToArray()
            : [request.ActorMembershipId];
    }

    internal static Task<Organization?> LockOrganizationAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return dbContext.Organizations
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organizations
                WHERE id = {organizationId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    internal static async Task<bool> LockActiveClientAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        Client? client = await dbContext.Clients
            .FromSqlInterpolated(
                $"""
                SELECT * FROM clients
                WHERE id = {clientId}
                  AND organization_id = {organizationId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);

        return client?.IsActive == true;
    }

    internal static async Task<bool> LockProcessAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid processId,
        CancellationToken cancellationToken)
    {
        LegalProcess? legalProcess = await dbContext.LegalProcesses
            .FromSqlInterpolated(
                $"""
                SELECT * FROM legal_processes
                WHERE id = {processId}
                  AND organization_id = {organizationId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);

        return legalProcess is not null;
    }
}
