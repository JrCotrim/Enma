using System.Data;
using Enma.Application.Auditing;
using Enma.Application.Clients;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class ClientMutationPersistence : IClientMutationPersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;
    private readonly TimeProvider _timeProvider;

    public ClientMutationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContextOptions = dbContextOptions;
        _timeProvider = timeProvider;
    }

    public Task<ClientMutationPersistenceResult> UpdateNameAsync(
        ClientMutationPersistenceRequest request,
        Func<ClientMutationLockedState, ClientMutationDecision> decide,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(
            request,
            decide,
            ClientMutationOperation.Rename,
            cancellationToken);
    }

    public Task<ClientMutationPersistenceResult> DeactivateAsync(
        ClientMutationPersistenceRequest request,
        Func<ClientMutationLockedState, ClientMutationDecision> decide,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(
            request,
            decide,
            ClientMutationOperation.Deactivate,
            cancellationToken);
    }

    public Task<ClientMutationPersistenceResult> ReactivateAsync(
        ClientMutationPersistenceRequest request,
        Func<ClientMutationLockedState, ClientMutationDecision> decide,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(
            request,
            decide,
            ClientMutationOperation.Reactivate,
            cancellationToken);
    }

    private async Task<ClientMutationPersistenceResult> MutateAsync(
        ClientMutationPersistenceRequest request,
        Func<ClientMutationLockedState, ClientMutationDecision> decide,
        ClientMutationOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decide);

        if (request.UserId == Guid.Empty ||
            request.OrganizationId == Guid.Empty ||
            request.ActorMembershipId == Guid.Empty ||
            request.ClientId == Guid.Empty ||
            !Enum.IsDefined(operation))
        {
            return ClientMutationPersistenceResult.AccessDenied;
        }

        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        Client? client = (await dbContext.Clients
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM clients
                    WHERE id = {request.ClientId}
                      AND organization_id = {request.OrganizationId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (client is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ClientMutationPersistenceResult.NotFound;
        }

        string oldName = client.Name;
        bool oldIsActive = client.IsActive;
        OrganizationMembership? actorMembership = await LockActorMembershipAsync(
            dbContext,
            request.OrganizationId,
            request.ActorMembershipId,
            cancellationToken);
        User? actorUser = actorMembership is null
            ? null
            : await LockActorUserAsync(
                dbContext,
                actorMembership.UserId,
                cancellationToken);
        Organization? organization = await LockOrganizationAsync(
            dbContext,
            request.OrganizationId,
            cancellationToken);
        ClientMutationDecision decision = decide(
            new ClientMutationLockedState(
                client,
                organization?.IsActive == true,
                CreateActorState(actorMembership, actorUser)));

        if (decision.Status != ClientMutationDecisionStatus.Persist)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ClientMutationPersistenceResult.AccessDenied;
        }

        AuditEventType? eventType = GetEventType(
            client,
            oldName,
            oldIsActive,
            operation);

        if (eventType is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ClientMutationPersistenceResult.Succeeded;
        }

        if (actorMembership is null)
        {
            throw new InvalidOperationException(
                "A client mutation persistence decision accepted a missing actor.");
        }

        TransactionalAuditActorContext auditActor =
            TransactionalAuditActorContext.FromValidatedMembership(actorMembership);
        AuditLogAppender.Append(
            dbContext,
            _timeProvider,
            auditActor,
            new AuditIntent(eventType.Value, client.Id));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ClientMutationPersistenceResult.Succeeded;
    }

    private static AuditEventType? GetEventType(
        Client client,
        string oldName,
        bool oldIsActive,
        ClientMutationOperation operation)
    {
        return operation switch
        {
            ClientMutationOperation.Rename when client.IsActive != oldIsActive =>
                throw new InvalidOperationException(
                    "A client rename decision changed lifecycle state."),
            ClientMutationOperation.Rename
                when !StringComparer.Ordinal.Equals(oldName, client.Name) =>
                AuditEventType.ClientRenamed,
            ClientMutationOperation.Rename => null,
            ClientMutationOperation.Deactivate or
                ClientMutationOperation.Reactivate
                when !StringComparer.Ordinal.Equals(oldName, client.Name) =>
                throw new InvalidOperationException(
                    "A client lifecycle decision changed the client name."),
            ClientMutationOperation.Deactivate when client.IsActive =>
                throw new InvalidOperationException(
                    "A client deactivation decision did not deactivate the client."),
            ClientMutationOperation.Deactivate when oldIsActive =>
                AuditEventType.ClientDeactivated,
            ClientMutationOperation.Deactivate => null,
            ClientMutationOperation.Reactivate when !client.IsActive =>
                throw new InvalidOperationException(
                    "A client reactivation decision did not reactivate the client."),
            ClientMutationOperation.Reactivate when !oldIsActive =>
                AuditEventType.ClientReactivated,
            ClientMutationOperation.Reactivate => null,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private static ClientLockedActorState? CreateActorState(
        OrganizationMembership? membership,
        User? user)
    {
        return membership is null
            ? null
            : new ClientLockedActorState(
                membership.Id,
                membership.OrganizationId,
                membership.UserId,
                membership.Role,
                membership.IsActive,
                user?.Id == membership.UserId && user.IsActive);
    }

    private static Task<OrganizationMembership?> LockActorMembershipAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid actorMembershipId,
        CancellationToken cancellationToken)
    {
        return dbContext.OrganizationMemberships
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organization_memberships
                WHERE organization_id = {organizationId}
                  AND id = {actorMembershipId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static Task<User?> LockActorUserAsync(
        EnmaDbContext dbContext,
        Guid userId,
        CancellationToken cancellationToken)
    {
        return dbContext.Users
            .FromSqlInterpolated(
                $"""
                SELECT * FROM users
                WHERE id = {userId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static Task<Organization?> LockOrganizationAsync(
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

    private enum ClientMutationOperation
    {
        Rename = 0,
        Deactivate = 1,
        Reactivate = 2
    }
}
