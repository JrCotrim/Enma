using System.Data;
using Enma.Application.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Enma.Infrastructure.Persistence;

public sealed class ExternalAuthenticationPersistence(
    DbContextOptions<EnmaDbContext> dbContextOptions,
    TimeProvider timeProvider) : IExternalAuthenticationPersistence
{
    public async Task<ExternalAuthenticationPersistenceResult> CompleteAsync(
        string provider,
        string providerSubject,
        string normalizedEmail,
        string? name,
        OrganizationInvitationTokenHash? invitationTokenHash,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = new EnmaDbContext(dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        ExternalAuthenticationPersistenceStatus invitationStatus =
            await ValidateInvitationAsync(
                dbContext,
                invitationTokenHash,
                normalizedEmail,
                cancellationToken);
        if (invitationStatus != ExternalAuthenticationPersistenceStatus.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(invitationStatus);
        }

        ExternalIdentity? linkedIdentity = await dbContext.ExternalIdentities
            .AsNoTracking()
            .SingleOrDefaultAsync(
                identity => identity.Provider == provider &&
                    identity.ProviderSubject == providerSubject,
                cancellationToken);

        if (linkedIdentity is not null)
        {
            ExternalAuthenticationPersistenceResult result =
                await LoadLinkedUserAsync(
                    dbContext,
                    linkedIdentity.UserId,
                    cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return result;
        }

        if (await dbContext.Users.AsNoTracking().AnyAsync(
                user => user.Email == normalizedEmail,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(ExternalAuthenticationPersistenceStatus.LinkRequired);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        User user;
        try
        {
            user = new User(name ?? string.Empty, normalizedEmail, now);
        }
        catch (ArgumentException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(ExternalAuthenticationPersistenceStatus.ProfileRequired);
        }

        user.VerifyEmail(now);
        var credential = new UserCredential(user.Id, passwordHash: null, now);
        var externalIdentity = new ExternalIdentity(
            user.Id,
            provider,
            providerSubject,
            now);

        dbContext.Users.Add(user);
        dbContext.UserCredentials.Add(credential);
        dbContext.ExternalIdentities.Add(externalIdentity);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                ExternalAuthenticationPersistenceStatus.Succeeded,
                user.Id,
                credential.CredentialVersion);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return await ResolveConcurrentCompletionAsync(
                provider,
                providerSubject,
                normalizedEmail,
                cancellationToken);
        }
    }

    public async Task<bool> LinkAsync(
        Guid userId,
        string provider,
        string providerSubject,
        string normalizedEmail,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = new EnmaDbContext(dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        User? user = await dbContext.Users
            .FromSqlInterpolated(
                $"SELECT * FROM users WHERE id = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null ||
            !user.IsActive ||
            user.EmailVerifiedAt is null ||
            !string.Equals(user.Email, normalizedEmail, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        ExternalIdentity? subjectIdentity = await dbContext.ExternalIdentities
            .AsNoTracking()
            .SingleOrDefaultAsync(
                identity => identity.Provider == provider &&
                    identity.ProviderSubject == providerSubject,
                cancellationToken);
        if (subjectIdentity is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return subjectIdentity.UserId == userId;
        }

        dbContext.ExternalIdentities.Add(new ExternalIdentity(
            userId,
            provider,
            providerSubject,
            timeProvider.GetUtcNow()));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
    }

    private async Task<ExternalAuthenticationPersistenceStatus>
        ValidateInvitationAsync(
            EnmaDbContext dbContext,
            OrganizationInvitationTokenHash? tokenHash,
            string normalizedEmail,
            CancellationToken cancellationToken)
    {
        if (tokenHash is null)
        {
            return ExternalAuthenticationPersistenceStatus.Succeeded;
        }

        InvitationLocator? locator = await dbContext.OrganizationInvitations
            .AsNoTracking()
            .Where(invitation => invitation.TokenHash != null &&
                invitation.TokenHash.Equals(tokenHash))
            .Select(invitation => new InvitationLocator(
                invitation.Id,
                invitation.OrganizationId))
            .SingleOrDefaultAsync(cancellationToken);
        if (locator is null)
        {
            return ExternalAuthenticationPersistenceStatus.InvalidInvitation;
        }

        Organization? organization = await dbContext.Organizations
            .FromSqlInterpolated(
                $"SELECT * FROM organizations WHERE id = {locator.OrganizationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        OrganizationInvitation? invitation = await dbContext.OrganizationInvitations
            .FromSqlInterpolated(
                $"SELECT * FROM organization_invitations WHERE organization_id = {locator.OrganizationId} AND id = {locator.InvitationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

        if (organization is null ||
            !organization.IsActive ||
            invitation is null ||
            invitation.TokenHash is null ||
            !invitation.TokenHash.Equals(tokenHash) ||
            invitation.GetState(timeProvider.GetUtcNow()) !=
                OrganizationInvitationState.Pending ||
            invitation.Role is not (
                OrganizationRole.Administrator or OrganizationRole.Member))
        {
            return ExternalAuthenticationPersistenceStatus.InvalidInvitation;
        }

        return string.Equals(
            invitation.InvitedEmail,
            normalizedEmail,
            StringComparison.Ordinal)
                ? ExternalAuthenticationPersistenceStatus.Succeeded
                : ExternalAuthenticationPersistenceStatus.WrongInvitationRecipient;
    }

    private async Task<ExternalAuthenticationPersistenceResult>
        ResolveConcurrentCompletionAsync(
            string provider,
            string providerSubject,
            string normalizedEmail,
            CancellationToken cancellationToken)
    {
        await using var dbContext = new EnmaDbContext(dbContextOptions);
        ExternalIdentity? identity = await dbContext.ExternalIdentities
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Provider == provider &&
                    candidate.ProviderSubject == providerSubject,
                cancellationToken);
        if (identity is not null)
        {
            return await LoadLinkedUserAsync(
                dbContext,
                identity.UserId,
                cancellationToken);
        }

        return await dbContext.Users.AsNoTracking().AnyAsync(
            user => user.Email == normalizedEmail,
            cancellationToken)
                ? new(ExternalAuthenticationPersistenceStatus.LinkRequired)
                : new(ExternalAuthenticationPersistenceStatus.Rejected);
    }

    private static async Task<ExternalAuthenticationPersistenceResult>
        LoadLinkedUserAsync(
            EnmaDbContext dbContext,
            Guid userId,
            CancellationToken cancellationToken)
    {
        var result = await (
            from user in dbContext.Users.AsNoTracking()
            join credential in dbContext.UserCredentials.AsNoTracking()
                on user.Id equals credential.UserId
            where user.Id == userId && user.IsActive && user.EmailVerifiedAt != null
            select new
            {
                user.Id,
                credential.CredentialVersion
            }).SingleOrDefaultAsync(cancellationToken);

        return result is null
            ? new(ExternalAuthenticationPersistenceStatus.Rejected)
            : new(
                ExternalAuthenticationPersistenceStatus.Succeeded,
                result.Id,
                result.CredentialVersion);
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation
                })
            {
                return true;
            }
        }

        return false;
    }

    private sealed record InvitationLocator(Guid InvitationId, Guid OrganizationId);
}
