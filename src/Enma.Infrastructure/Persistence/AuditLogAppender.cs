using System.Diagnostics;
using Enma.Application.Auditing;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;

namespace Enma.Infrastructure.Persistence;

/// <summary>
/// A snapshot of actor data already revalidated from locked rows by the current
/// infrastructure transaction. It does not perform or grant authorization.
/// </summary>
internal sealed class TransactionalAuditActorContext
{
    private TransactionalAuditActorContext(
        Guid organizationId,
        Guid userId,
        Guid membershipId,
        OrganizationRole role)
    {
        OrganizationId = organizationId;
        UserId = userId;
        MembershipId = membershipId;
        Role = role;
    }

    public Guid OrganizationId { get; }

    public Guid UserId { get; }

    public Guid MembershipId { get; }

    public OrganizationRole Role { get; }

    public static TransactionalAuditActorContext FromValidatedMembership(
        OrganizationMembership validatedMembership)
    {
        ArgumentNullException.ThrowIfNull(validatedMembership);

        return new TransactionalAuditActorContext(
            validatedMembership.OrganizationId,
            validatedMembership.UserId,
            validatedMembership.Id,
            validatedMembership.Role);
    }
}

internal static class AuditLogAppender
{
    public static void Append(
        EnmaDbContext dbContext,
        TimeProvider timeProvider,
        TransactionalAuditActorContext actor,
        AuditIntent intent)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(intent);

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "An active transaction is required to append an audit log.");
        }

        ActivityTraceId traceId = Activity.Current?.TraceId ?? default;
        AuditLog auditLog = AuditLog.CreateAuthoritative(
            Guid.NewGuid(),
            actor.OrganizationId,
            actor.UserId,
            actor.MembershipId,
            actor.Role,
            intent.EventType,
            intent.EntityType,
            intent.EntityId,
            timeProvider.GetUtcNow(),
            intent.Details,
            traceId == default ? null : traceId.ToHexString());

        dbContext.AuditLogs.Add(auditLog);
    }
}
