using System.Data;
using Enma.Application.Auditing;
using Enma.Application.Organizations.Invitations;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Enma.Infrastructure.Persistence;

public sealed class OrganizationInvitationMutationPersistence
    : IOrganizationInvitationMutationPersistence
{
    private const int MaximumTokenGenerationAttempts = 3;
    private const string LockNotAvailableSqlState = "55P03";
    private const string OpenInvitationConstraint =
        "ux_organization_invitations_open_organization_id_email";
    private const string TokenHashConstraint =
        "ux_organization_invitations_token_hash";

    private readonly DbContextOptions<EnmaDbContext> dbContextOptions;
    private readonly TimeProvider timeProvider;
    private readonly IOrganizationInvitationTokenService tokenService;

    public OrganizationInvitationMutationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider,
        IOrganizationInvitationTokenService tokenService)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(tokenService);

        this.dbContextOptions = dbContextOptions;
        this.timeProvider = timeProvider;
        this.tokenService = tokenService;
    }

    public async Task<CreateOrganizationInvitationPersistenceResult> CreateAsync(
        CreateOrganizationInvitationPersistenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string email;

        try
        {
            email = User.NormalizeEmail(request.Email);
        }
        catch (ArgumentException)
        {
            return new CreateOrganizationInvitationPersistenceResult(
                CreateOrganizationInvitationPersistenceStatus.InvalidInput);
        }

        if (!HasValidActorRequest(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) ||
            !IsInvitableRole(request.Role))
        {
            return new CreateOrganizationInvitationPersistenceResult(
                CreateOrganizationInvitationPersistenceStatus.InvalidInput);
        }

        int tokenGenerationAttempt = 1;

        while (true)
        {
            try
            {
                return await ExecuteCreateAttemptAsync(
                    request with { Email = email },
                    cancellationToken);
            }
            catch (Exception exception) when (IsLockNotAvailable(exception))
            {
                await WaitForCreateMembershipLocksAsync(
                    request with { Email = email },
                    cancellationToken);
            }
            catch (DbUpdateException exception) when (
                IsUniqueViolation(exception, TokenHashConstraint) &&
                tokenGenerationAttempt < MaximumTokenGenerationAttempts)
            {
                tokenGenerationAttempt++;
                // Generate a fresh token in a fresh transaction.
            }
            catch (DbUpdateException exception) when (
                IsUniqueViolation(exception, OpenInvitationConstraint))
            {
                return new CreateOrganizationInvitationPersistenceResult(
                    CreateOrganizationInvitationPersistenceStatus
                        .DuplicatePendingInvitation);
            }
        }
    }

    public async Task<RevokeOrganizationInvitationPersistenceResult> RevokeAsync(
        OrganizationInvitationMutationPersistenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!HasValidMutationRequest(request))
        {
            return RevokeOrganizationInvitationPersistenceResult.InvalidInput;
        }

        while (true)
        {
            try
            {
                return await ExecuteRevokeAttemptAsync(request, cancellationToken);
            }
            catch (Exception exception) when (IsLockNotAvailable(exception))
            {
                await WaitForActorMembershipLockAsync(request, cancellationToken);
            }
        }
    }

    private async Task<RevokeOrganizationInvitationPersistenceResult>
        ExecuteRevokeAttemptAsync(
            OrganizationInvitationMutationPersistenceRequest request,
            CancellationToken cancellationToken)
    {
        await using var dbContext = new EnmaDbContext(dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        Organization? organization = await LockOrganizationAsync(
            dbContext,
            request.OrganizationId,
            cancellationToken);
        OrganizationInvitation? invitation = await LockInvitationAsync(
            dbContext,
            request.OrganizationId,
            request.InvitationId,
            cancellationToken);
        OrganizationMembership? actorMembership = await LockActorMembershipAsync(
            dbContext,
            request.OrganizationId,
            request.ActorMembershipId,
            nowait: true,
            cancellationToken);
        User? actorUser = await LockActorUserAsync(
            dbContext,
            request.UserId,
            cancellationToken);

        if (!IsValidActor(
                organization,
                actorMembership,
                actorUser,
                request.UserId,
                request.OrganizationId))
        {
            await transaction.RollbackAsync(cancellationToken);
            return RevokeOrganizationInvitationPersistenceResult.AccessDenied;
        }

        if (invitation is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return RevokeOrganizationInvitationPersistenceResult.NotFound;
        }

        if (!CanManageRole(actorMembership!.Role, invitation.Role))
        {
            await transaction.RollbackAsync(cancellationToken);
            return RevokeOrganizationInvitationPersistenceResult.AccessDenied;
        }

        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        OrganizationInvitationState state = invitation.GetState(now);

        if (state == OrganizationInvitationState.Revoked)
        {
            await transaction.CommitAsync(cancellationToken);
            return RevokeOrganizationInvitationPersistenceResult.Succeeded;
        }

        if (state == OrganizationInvitationState.Expired)
        {
            if (invitation.ExpiredAt is null)
            {
                invitation.Expire(now);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return RevokeOrganizationInvitationPersistenceResult.Conflict;
        }

        if (state == OrganizationInvitationState.Accepted)
        {
            await transaction.RollbackAsync(cancellationToken);
            return RevokeOrganizationInvitationPersistenceResult.Conflict;
        }

        invitation.Revoke(now);
        AppendAudit(
            dbContext,
            actorMembership,
            new AuditIntent(
                AuditEventType.OrganizationInvitationRevoked,
                invitation.Id));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return RevokeOrganizationInvitationPersistenceResult.Succeeded;
    }

    public async Task<ResendOrganizationInvitationPersistenceResult> ResendAsync(
        OrganizationInvitationMutationPersistenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!HasValidMutationRequest(request))
        {
            return new ResendOrganizationInvitationPersistenceResult(
                ResendOrganizationInvitationPersistenceStatus.InvalidInput);
        }

        int tokenGenerationAttempt = 1;

        while (true)
        {
            try
            {
                return await ExecuteResendAttemptAsync(
                    request,
                    cancellationToken);
            }
            catch (Exception exception) when (IsLockNotAvailable(exception))
            {
                await WaitForActorMembershipLockAsync(request, cancellationToken);
            }
            catch (DbUpdateException exception) when (
                IsUniqueViolation(exception, TokenHashConstraint) &&
                tokenGenerationAttempt < MaximumTokenGenerationAttempts)
            {
                tokenGenerationAttempt++;
                // Generate a fresh token in a fresh transaction.
            }
        }
    }

    private async Task<CreateOrganizationInvitationPersistenceResult>
        ExecuteCreateAttemptAsync(
            CreateOrganizationInvitationPersistenceRequest request,
            CancellationToken cancellationToken)
    {
        await using var dbContext = new EnmaDbContext(dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        Organization? organization = await LockOrganizationAsync(
            dbContext,
            request.OrganizationId,
            cancellationToken);
        OrganizationInvitation? openInvitation =
            await LockOpenInvitationAsync(
                dbContext,
                request.OrganizationId,
                request.Email,
                cancellationToken);
        Guid? invitedUserId = await FindUserIdByEmailAsync(
            dbContext,
            request.Email,
            cancellationToken);
        IReadOnlyList<OrganizationMembership> memberships =
            await LockCreateMembershipsAsync(
                dbContext,
                request.OrganizationId,
                request.ActorMembershipId,
                invitedUserId,
                nowait: true,
                cancellationToken);
        IReadOnlyDictionary<Guid, User> users = await LockUsersAsync(
            dbContext,
            request.UserId,
            invitedUserId,
            cancellationToken);

        OrganizationMembership? actorMembership = memberships.SingleOrDefault(
            membership => membership.Id == request.ActorMembershipId);
        users.TryGetValue(request.UserId, out User? actorUser);

        if (!IsValidActor(
                organization,
                actorMembership,
                actorUser,
                request.UserId,
                request.OrganizationId) ||
            !CanManageRole(actorMembership!.Role, request.Role))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CreateOrganizationInvitationPersistenceResult(
                CreateOrganizationInvitationPersistenceStatus.AccessDenied);
        }

        OrganizationMembership? invitedMembership = invitedUserId is Guid userId &&
            users.TryGetValue(userId, out User? invitedUser) &&
            string.Equals(
                invitedUser.Email,
                request.Email,
                StringComparison.Ordinal)
                ? memberships.SingleOrDefault(membership =>
                    membership.UserId == userId)
                : null;

        if (invitedMembership?.IsActive == true)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CreateOrganizationInvitationPersistenceResult(
                CreateOrganizationInvitationPersistenceStatus
                    .ExistingActiveMembership);
        }

        if (invitedMembership is not null &&
            invitedMembership.Role != request.Role)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CreateOrganizationInvitationPersistenceResult(
                CreateOrganizationInvitationPersistenceStatus
                    .IncompatibleInactiveMembership);
        }

        if (openInvitation is not null)
        {
            if (openInvitation.GetState(now) ==
                OrganizationInvitationState.Pending)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CreateOrganizationInvitationPersistenceResult(
                    CreateOrganizationInvitationPersistenceStatus
                        .DuplicatePendingInvitation);
            }

            openInvitation.Expire(now);
        }

        string rawToken = tokenService.GenerateToken(out var tokenHash);
        DateTimeOffset expiresAt = now.Add(
            OrganizationInvitationPolicy.TokenLifetime);
        var invitation = new OrganizationInvitation(
            request.OrganizationId,
            request.Email,
            request.Role,
            actorMembership.Id,
            tokenHash,
            now,
            now,
            expiresAt);
        dbContext.OrganizationInvitations.Add(invitation);
        AppendAudit(
            dbContext,
            actorMembership,
            new AuditIntent(
                AuditEventType.OrganizationInvitationCreated,
                invitation.Id,
                new OrganizationInvitationCreatedAuditDetails(invitation.Role)));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CreateOrganizationInvitationPersistenceResult(
            CreateOrganizationInvitationPersistenceStatus.Succeeded,
            invitation.Id,
            new OrganizationInvitationDeliveryRequest(
                invitation.InvitedEmail,
                organization!.Name,
                invitation.Role,
                invitation.ExpiresAt,
                rawToken));
    }

    private async Task<ResendOrganizationInvitationPersistenceResult>
        ExecuteResendAttemptAsync(
            OrganizationInvitationMutationPersistenceRequest request,
            CancellationToken cancellationToken)
    {
        await using var dbContext = new EnmaDbContext(dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        Organization? organization = await LockOrganizationAsync(
            dbContext,
            request.OrganizationId,
            cancellationToken);
        OrganizationInvitation? invitation = await LockInvitationAsync(
            dbContext,
            request.OrganizationId,
            request.InvitationId,
            cancellationToken);
        OrganizationMembership? actorMembership = await LockActorMembershipAsync(
            dbContext,
            request.OrganizationId,
            request.ActorMembershipId,
            nowait: true,
            cancellationToken);
        User? actorUser = await LockActorUserAsync(
            dbContext,
            request.UserId,
            cancellationToken);

        if (!IsValidActor(
                organization,
                actorMembership,
                actorUser,
                request.UserId,
                request.OrganizationId))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ResendOrganizationInvitationPersistenceResult(
                ResendOrganizationInvitationPersistenceStatus.AccessDenied);
        }

        if (invitation is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ResendOrganizationInvitationPersistenceResult(
                ResendOrganizationInvitationPersistenceStatus.NotFound);
        }

        if (!CanManageRole(actorMembership!.Role, invitation.Role))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ResendOrganizationInvitationPersistenceResult(
                ResendOrganizationInvitationPersistenceStatus.AccessDenied);
        }

        DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
        OrganizationInvitationState state = invitation.GetState(now);

        if (state == OrganizationInvitationState.Expired)
        {
            if (invitation.ExpiredAt is null)
            {
                invitation.Expire(now);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new ResendOrganizationInvitationPersistenceResult(
                ResendOrganizationInvitationPersistenceStatus.Conflict);
        }

        if (state != OrganizationInvitationState.Pending)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ResendOrganizationInvitationPersistenceResult(
                ResendOrganizationInvitationPersistenceStatus.Conflict);
        }

        DateTimeOffset cooldownEndsAt = invitation.TokenIssuedAt.Add(
            OrganizationInvitationPolicy.ResendCooldown);

        if (now < cooldownEndsAt)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ResendOrganizationInvitationPersistenceResult(
                ResendOrganizationInvitationPersistenceStatus.Cooldown,
                RetryAfter: cooldownEndsAt - now);
        }

        string rawToken = tokenService.GenerateToken(out var tokenHash);
        invitation.RotateToken(
            tokenHash,
            now,
            now.Add(OrganizationInvitationPolicy.TokenLifetime));
        AppendAudit(
            dbContext,
            actorMembership,
            new AuditIntent(
                AuditEventType.OrganizationInvitationResent,
                invitation.Id));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ResendOrganizationInvitationPersistenceResult(
            ResendOrganizationInvitationPersistenceStatus.Succeeded,
            new OrganizationInvitationDeliveryRequest(
                invitation.InvitedEmail,
                organization!.Name,
                invitation.Role,
                invitation.ExpiresAt,
                rawToken));
    }

    private void AppendAudit(
        EnmaDbContext dbContext,
        OrganizationMembership actorMembership,
        AuditIntent intent)
    {
        AuditLogAppender.Append(
            dbContext,
            timeProvider,
            TransactionalAuditActorContext.FromValidatedMembership(
                actorMembership),
            intent);
    }

    private async Task WaitForCreateMembershipLocksAsync(
        CreateOrganizationInvitationPersistenceRequest request,
        CancellationToken cancellationToken)
    {
        await using var dbContext = new EnmaDbContext(dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
        Guid? invitedUserId = await FindUserIdByEmailAsync(
            dbContext,
            request.Email,
            cancellationToken);

        await LockCreateMembershipsAsync(
            dbContext,
            request.OrganizationId,
            request.ActorMembershipId,
            invitedUserId,
            nowait: false,
            cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
    }

    private async Task WaitForActorMembershipLockAsync(
        OrganizationInvitationMutationPersistenceRequest request,
        CancellationToken cancellationToken)
    {
        await using var dbContext = new EnmaDbContext(dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        await LockActorMembershipAsync(
            dbContext,
            request.OrganizationId,
            request.ActorMembershipId,
            nowait: false,
            cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
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

    private static Task<OrganizationInvitation?> LockOpenInvitationAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        string email,
        CancellationToken cancellationToken)
    {
        return dbContext.OrganizationInvitations
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organization_invitations
                WHERE organization_id = {organizationId}
                  AND invited_email = {email}
                  AND accepted_at IS NULL
                  AND revoked_at IS NULL
                  AND expired_at IS NULL
                ORDER BY id
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static Task<OrganizationInvitation?> LockInvitationAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        return dbContext.OrganizationInvitations
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organization_invitations
                WHERE organization_id = {organizationId}
                  AND id = {invitationId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static Task<Guid?> FindUserIdByEmailAsync(
        EnmaDbContext dbContext,
        string email,
        CancellationToken cancellationToken)
    {
        return dbContext.Users
            .AsNoTracking()
            .Where(user => user.Email == email)
            .Select(user => (Guid?)user.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static Task<List<OrganizationMembership>> LockCreateMembershipsAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid actorMembershipId,
        Guid? invitedUserId,
        bool nowait,
        CancellationToken cancellationToken)
    {
        if (invitedUserId is Guid nowaitUserId && nowait)
        {
            return dbContext.OrganizationMemberships
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organization_memberships
                    WHERE organization_id = {organizationId}
                      AND (id = {actorMembershipId} OR user_id = {nowaitUserId})
                    ORDER BY id
                    FOR UPDATE NOWAIT
                    """)
                .ToListAsync(cancellationToken);
        }

        if (nowait)
        {
            return dbContext.OrganizationMemberships
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organization_memberships
                    WHERE organization_id = {organizationId}
                      AND id = {actorMembershipId}
                    ORDER BY id
                    FOR UPDATE NOWAIT
                    """)
                .ToListAsync(cancellationToken);
        }

        return invitedUserId is Guid userId
            ? dbContext.OrganizationMemberships
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organization_memberships
                    WHERE organization_id = {organizationId}
                      AND (id = {actorMembershipId} OR user_id = {userId})
                    ORDER BY id
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken)
            : dbContext.OrganizationMemberships
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organization_memberships
                    WHERE organization_id = {organizationId}
                      AND id = {actorMembershipId}
                    ORDER BY id
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken);
    }

    private static Task<OrganizationMembership?> LockActorMembershipAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        Guid membershipId,
        bool nowait,
        CancellationToken cancellationToken)
    {
        if (nowait)
        {
            return dbContext.OrganizationMemberships
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organization_memberships
                    WHERE organization_id = {organizationId}
                      AND id = {membershipId}
                    FOR UPDATE NOWAIT
                    """)
                .SingleOrDefaultAsync(cancellationToken);
        }

        return dbContext.OrganizationMemberships
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organization_memberships
                WHERE organization_id = {organizationId}
                  AND id = {membershipId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static async Task<IReadOnlyDictionary<Guid, User>> LockUsersAsync(
        EnmaDbContext dbContext,
        Guid actorUserId,
        Guid? invitedUserId,
        CancellationToken cancellationToken)
    {
        Guid[] userIds = invitedUserId is Guid targetUserId
            ? [actorUserId, targetUserId]
            : [actorUserId];
        userIds = userIds.Distinct().OrderBy(userId => userId).ToArray();

        return (await dbContext.Users
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM users
                    WHERE id = ANY ({userIds})
                    ORDER BY id
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .ToDictionary(user => user.Id);
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

    private static bool IsValidActor(
        Organization? organization,
        OrganizationMembership? membership,
        User? user,
        Guid expectedUserId,
        Guid expectedOrganizationId)
    {
        return organization?.Id == expectedOrganizationId &&
            organization.IsActive &&
            membership?.OrganizationId == expectedOrganizationId &&
            membership.UserId == expectedUserId &&
            membership.IsActive &&
            membership.Role is OrganizationRole.Owner or
                OrganizationRole.Administrator &&
            user?.Id == expectedUserId &&
            user.IsActive;
    }

    private static bool CanManageRole(
        OrganizationRole actorRole,
        OrganizationRole targetRole)
    {
        return (actorRole, targetRole) switch
        {
            (OrganizationRole.Owner,
                OrganizationRole.Administrator or OrganizationRole.Member) => true,
            (OrganizationRole.Administrator, OrganizationRole.Member) => true,
            _ => false
        };
    }

    private static bool IsInvitableRole(OrganizationRole role)
    {
        return role is OrganizationRole.Administrator or OrganizationRole.Member;
    }

    private static bool HasValidActorRequest(
        Guid userId,
        Guid organizationId,
        Guid actorMembershipId)
    {
        return userId != Guid.Empty &&
            organizationId != Guid.Empty &&
            actorMembershipId != Guid.Empty;
    }

    private static bool HasValidMutationRequest(
        OrganizationInvitationMutationPersistenceRequest request)
    {
        return HasValidActorRequest(
                request.UserId,
                request.OrganizationId,
                request.ActorMembershipId) &&
            request.InvitationId != Guid.Empty;
    }

    private static bool IsUniqueViolation(
        Exception exception,
        string constraintName)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation
                } postgresException &&
                string.Equals(
                    postgresException.ConstraintName,
                    constraintName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLockNotAvailable(Exception exception)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is PostgresException postgresException &&
                postgresException.SqlState == LockNotAvailableSqlState)
            {
                return true;
            }
        }

        return false;
    }
}
