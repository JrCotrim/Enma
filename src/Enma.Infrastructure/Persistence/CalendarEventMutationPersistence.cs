using System.Data;
using Enma.Application.Auditing;
using Enma.Application.CalendarEvents;
using Enma.Domain.Auditing;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class CalendarEventMutationPersistence
    : ICalendarEventMutationPersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;
    private readonly TimeProvider _timeProvider;

    public CalendarEventMutationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContextOptions = dbContextOptions;
        _timeProvider = timeProvider;
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
            forcedClientId: null,
            forcedProcessId: null,
            allowAssigneeRetry: true,
            allowAssociationRetry: true,
            cancellationToken);
    }

    private async Task<CalendarEventMutationPersistenceResult> ExecuteAttemptAsync(
        CalendarEventMutationPersistenceRequest request,
        Func<CalendarEventMutationLockedState, CalendarEventMutationDecision> decide,
        Guid? forcedAssigneeMembershipId,
        Guid? forcedClientId,
        Guid? forcedProcessId,
        bool allowAssigneeRetry,
        bool allowAssociationRetry,
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

        string oldTitle = calendarEvent.Title;
        string? oldDescription = calendarEvent.Description;
        DateTimeOffset oldStartsAt = calendarEvent.StartsAt;
        DateTimeOffset oldEndsAt = calendarEvent.EndsAt;
        string? oldLocation = calendarEvent.Location;
        Guid? oldClientId = calendarEvent.ClientId;
        Guid? oldProcessId = calendarEvent.ProcessId;
        Guid? oldAssigneeMembershipId = calendarEvent.AssigneeMembershipId;

        if (forcedClientId is not null && forcedProcessId is not null)
        {
            throw new InvalidOperationException(
                "Calendar event mutation cannot lock two associations.");
        }

        bool? isClientAvailable = forcedClientId is Guid clientId
            ? await CalendarEventCreationPersistence.LockActiveClientAsync(
                dbContext,
                request.OrganizationId,
                clientId,
                cancellationToken)
            : null;
        bool? isProcessAvailable = forcedProcessId is Guid processId
            ? await CalendarEventCreationPersistence.LockProcessAsync(
                dbContext,
                request.OrganizationId,
                processId,
                cancellationToken)
            : null;
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
        Organization? organization =
            await CalendarEventCreationPersistence.LockOrganizationAsync(
                dbContext,
                request.OrganizationId,
                cancellationToken);
        var state = new CalendarEventMutationLockedState(
            calendarEvent,
            organization?.IsActive == true,
            actor,
            AssociationLookupPerformed:
                forcedClientId is not null || forcedProcessId is not null,
            ValidatedClientId: forcedClientId,
            IsClientAvailable: isClientAvailable,
            ValidatedProcessId: forcedProcessId,
            IsProcessAvailable: isProcessAvailable,
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
                forcedClientId,
                forcedProcessId,
                allowAssigneeRetry: false,
                allowAssociationRetry,
                cancellationToken);
        }

        if (decision.Status == CalendarEventMutationDecisionStatus.ValidateAssociation)
        {
            if (!allowAssociationRetry ||
                forcedClientId is not null ||
                forcedProcessId is not null ||
                decision.ClientId is null && decision.ProcessId is null ||
                decision.ClientId is not null && decision.ProcessId is not null)
            {
                throw new InvalidOperationException(
                    "Calendar event mutation requested an invalid association retry.");
            }

            await transaction.RollbackAsync(cancellationToken);
            return await ExecuteAttemptAsync(
                request,
                decide,
                forcedAssigneeMembershipId,
                decision.ClientId,
                decision.ProcessId,
                allowAssigneeRetry,
                allowAssociationRetry: false,
                cancellationToken);
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

        OrganizationMembership actorMembership =
            identities.MembershipsById[request.ActorMembershipId];
        AppendAuditLogs(
            dbContext,
            actorMembership,
            calendarEvent,
            decision.Status,
            oldTitle,
            oldDescription,
            oldStartsAt,
            oldEndsAt,
            oldLocation,
            oldClientId,
            oldProcessId,
            oldAssigneeMembershipId);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return decision.Status == CalendarEventMutationDecisionStatus.Delete
            ? CalendarEventMutationPersistenceResult.Deleted
            : CalendarEventMutationPersistenceResult.Succeeded;
    }

    private void AppendAuditLogs(
        EnmaDbContext dbContext,
        OrganizationMembership actorMembership,
        CalendarEvent calendarEvent,
        CalendarEventMutationDecisionStatus decisionStatus,
        string oldTitle,
        string? oldDescription,
        DateTimeOffset oldStartsAt,
        DateTimeOffset oldEndsAt,
        string? oldLocation,
        Guid? oldClientId,
        Guid? oldProcessId,
        Guid? oldAssigneeMembershipId)
    {
        TransactionalAuditActorContext auditActor =
            TransactionalAuditActorContext.FromValidatedMembership(actorMembership);

        if (decisionStatus == CalendarEventMutationDecisionStatus.Delete)
        {
            AuditLogAppender.Append(
                dbContext,
                _timeProvider,
                auditActor,
                new AuditIntent(
                    AuditEventType.CalendarEventDeleted,
                    calendarEvent.Id));
            return;
        }

        var changedFields = new List<CalendarEventChangedField>(7);

        if (!StringComparer.Ordinal.Equals(oldTitle, calendarEvent.Title))
        {
            changedFields.Add(CalendarEventChangedField.Title);
        }

        if (!StringComparer.Ordinal.Equals(oldDescription, calendarEvent.Description))
        {
            changedFields.Add(CalendarEventChangedField.Description);
        }

        if (oldStartsAt != calendarEvent.StartsAt)
        {
            changedFields.Add(CalendarEventChangedField.StartsAt);
        }

        if (oldEndsAt != calendarEvent.EndsAt)
        {
            changedFields.Add(CalendarEventChangedField.EndsAt);
        }

        if (!StringComparer.Ordinal.Equals(oldLocation, calendarEvent.Location))
        {
            changedFields.Add(CalendarEventChangedField.Location);
        }

        if (oldClientId != calendarEvent.ClientId)
        {
            changedFields.Add(CalendarEventChangedField.ClientId);
        }

        if (oldProcessId != calendarEvent.ProcessId)
        {
            changedFields.Add(CalendarEventChangedField.ProcessId);
        }

        if (changedFields.Count > 0)
        {
            AuditLogAppender.Append(
                dbContext,
                _timeProvider,
                auditActor,
                new AuditIntent(
                    AuditEventType.CalendarEventUpdated,
                    calendarEvent.Id,
                    new CalendarEventUpdatedAuditDetails(changedFields)));
        }

        if (oldAssigneeMembershipId != calendarEvent.AssigneeMembershipId)
        {
            AuditLogAppender.Append(
                dbContext,
                _timeProvider,
                auditActor,
                new AuditIntent(
                    AuditEventType.CalendarEventAssigneeChanged,
                    calendarEvent.Id,
                    new CalendarEventAssigneeChangedAuditDetails(
                        oldAssigneeMembershipId,
                        calendarEvent.AssigneeMembershipId)));
        }
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
