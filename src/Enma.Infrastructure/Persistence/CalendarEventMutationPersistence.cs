using System.Data;
using Enma.Application.CalendarEvents;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class CalendarEventMutationPersistence
    : ICalendarEventMutationPersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;

    public CalendarEventMutationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        _dbContextOptions = dbContextOptions;
    }

    public Task<CalendarEventMutationPersistenceResult> ExecuteAsync(
        CalendarEventMutationPersistenceRequest request,
        Func<CalendarEventMutationLockedState, CalendarEventMutationDecision> decide,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decide);

        return ExecuteAttemptAsync(
            request,
            decide,
            forcedAssigneeMembershipId: null,
            allowAssigneeRetry: true,
            cancellationToken);
    }

    private async Task<CalendarEventMutationPersistenceResult> ExecuteAttemptAsync(
        CalendarEventMutationPersistenceRequest request,
        Func<CalendarEventMutationLockedState, CalendarEventMutationDecision> decide,
        Guid? forcedAssigneeMembershipId,
        bool allowAssigneeRetry,
        CancellationToken cancellationToken)
    {
        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        CalendarEvent? calendarEvent = await LockCalendarEventAsync(
            dbContext,
            request.CalendarEventId,
            request.OrganizationId,
            cancellationToken);

        if (calendarEvent is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return CalendarEventMutationPersistenceResult.NotFound;
        }

        Organization? organization =
            await CalendarEventCreationPersistence.LockOrganizationAsync(
                dbContext,
                request.OrganizationId,
                cancellationToken);
        IEnumerable<Guid> membershipIds = forcedAssigneeMembershipId is Guid assigneeId
            ? [request.ActorMembershipId, assigneeId]
            : [request.ActorMembershipId];
        CalendarEventLockedIdentities identities =
            await CalendarEventIdentityLocking.LockAsync(
                dbContext,
                request.OrganizationId,
                membershipIds,
                cancellationToken);
        CalendarEventMemberState? actor =
            CalendarEventIdentityLocking.CreateMemberState(
                request.ActorMembershipId,
                identities);
        CalendarEventMemberState? assignee =
            forcedAssigneeMembershipId is Guid lockedAssigneeId
                ? CalendarEventIdentityLocking.CreateMemberState(
                    lockedAssigneeId,
                    identities)
                : null;
        var state = new CalendarEventMutationLockedState(
            calendarEvent,
            organization?.IsActive == true,
            actor,
            AssociationLookupPerformed: false,
            ValidatedClientId: null,
            IsClientAvailable: null,
            ValidatedProcessId: null,
            IsProcessAvailable: null,
            AssigneeLookupPerformed: forcedAssigneeMembershipId is not null,
            ValidatedAssigneeMembershipId: forcedAssigneeMembershipId,
            Assignee: assignee);

        CalendarEventMutationDecision decision = decide(state);

        if (decision.Status == CalendarEventMutationDecisionStatus.ValidateAssignee)
        {
            if (!allowAssigneeRetry ||
                decision.AssigneeMembershipId is not Guid requestedAssigneeId)
            {
                throw new InvalidOperationException(
                    "Calendar event mutation requested an invalid assignee retry.");
            }

            await transaction.RollbackAsync(cancellationToken);
            return await ExecuteAttemptAsync(
                request,
                decide,
                requestedAssigneeId,
                allowAssigneeRetry: false,
                cancellationToken);
        }

        if (decision.Status == CalendarEventMutationDecisionStatus.ValidateAssociation)
        {
            state = await ValidateAssociationAsync(
                dbContext,
                request.OrganizationId,
                state,
                decision,
                cancellationToken);
            decision = decide(state);

            if (decision.Status is
                CalendarEventMutationDecisionStatus.ValidateAssociation or
                CalendarEventMutationDecisionStatus.ValidateAssignee)
            {
                throw new InvalidOperationException(
                    "Calendar event mutation requested repeated relation validation.");
            }
        }

        if (decision.Status is not (
            CalendarEventMutationDecisionStatus.Persist or
            CalendarEventMutationDecisionStatus.Delete))
        {
            await transaction.RollbackAsync(cancellationToken);
            return MapRejectedDecision(decision.Status);
        }

        if (decision.Status == CalendarEventMutationDecisionStatus.Delete)
        {
            dbContext.CalendarEvents.Remove(calendarEvent);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return decision.Status == CalendarEventMutationDecisionStatus.Delete
            ? CalendarEventMutationPersistenceResult.Deleted
            : CalendarEventMutationPersistenceResult.Succeeded;
    }

    private static async Task<CalendarEventMutationLockedState>
        ValidateAssociationAsync(
            EnmaDbContext dbContext,
            Guid organizationId,
            CalendarEventMutationLockedState state,
            CalendarEventMutationDecision decision,
            CancellationToken cancellationToken)
    {
        if (decision.ClientId is Guid clientId)
        {
            bool isAvailable =
                await CalendarEventCreationPersistence.LockActiveClientAsync(
                    dbContext,
                    organizationId,
                    clientId,
                    cancellationToken);
            return state with
            {
                AssociationLookupPerformed = true,
                ValidatedClientId = clientId,
                IsClientAvailable = isAvailable
            };
        }

        if (decision.ProcessId is Guid processId)
        {
            bool isAvailable =
                await CalendarEventCreationPersistence.LockProcessAsync(
                    dbContext,
                    organizationId,
                    processId,
                    cancellationToken);
            return state with
            {
                AssociationLookupPerformed = true,
                ValidatedProcessId = processId,
                IsProcessAvailable = isAvailable
            };
        }

        throw new InvalidOperationException(
            "Calendar event mutation requested invalid association validation.");
    }

    private static async Task<CalendarEvent?> LockCalendarEventAsync(
        EnmaDbContext dbContext,
        Guid calendarEventId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return (await dbContext.CalendarEvents
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM calendar_events
                    WHERE id = {calendarEventId}
                      AND organization_id = {organizationId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();
    }

    private static CalendarEventMutationPersistenceResult MapRejectedDecision(
        CalendarEventMutationDecisionStatus status)
    {
        return status switch
        {
            CalendarEventMutationDecisionStatus.AccessDenied =>
                CalendarEventMutationPersistenceResult.AccessDenied,
            CalendarEventMutationDecisionStatus.RelatedClientUnavailable =>
                CalendarEventMutationPersistenceResult.RelatedClientUnavailable,
            CalendarEventMutationDecisionStatus.RelatedProcessUnavailable =>
                CalendarEventMutationPersistenceResult.RelatedProcessUnavailable,
            CalendarEventMutationDecisionStatus.RelatedAssigneeUnavailable =>
                CalendarEventMutationPersistenceResult.RelatedAssigneeUnavailable,
            CalendarEventMutationDecisionStatus.InvalidInput =>
                CalendarEventMutationPersistenceResult.InvalidInput,
            _ => throw new InvalidOperationException(
                "Calendar event mutation returned an invalid rejection decision.")
        };
    }
}
