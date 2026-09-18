using System.Data;
using Enma.Application.Onboarding.RegisterInvitedUser;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Enma.Infrastructure.Persistence;

public sealed class InvitedUserRegistrationPersistence
    : IInvitedUserRegistrationPersistence
{
    private const string UserEmailConstraint = "ux_users_email";
    private readonly DbContextOptions<EnmaDbContext> dbContextOptions;
    private readonly TimeProvider timeProvider;

    public InvitedUserRegistrationPersistence(
        DbContextOptions<EnmaDbContext> dbContextOptions,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.dbContextOptions = dbContextOptions;
        this.timeProvider = timeProvider;
    }

    public async Task<InvitedUserRegistrationPersistenceResult> RegisterAsync(
        OrganizationInvitationTokenHash invitationTokenHash,
        User user,
        UserCredential credential,
        EmailVerificationChallenge verificationChallenge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invitationTokenHash);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(verificationChallenge);

        if (credential.UserId != user.Id ||
            verificationChallenge.UserId != user.Id ||
            !string.Equals(
                verificationChallenge.EmailAtIssue,
                user.Email,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Invited user registration entities do not share one identity.");
        }

        await using var lookupContext = new EnmaDbContext(dbContextOptions);
        InvitationLocator? locator = await lookupContext.OrganizationInvitations
            .AsNoTracking()
            .Where(invitation => invitation.TokenHash != null &&
                invitation.TokenHash.Equals(invitationTokenHash))
            .Select(invitation => new InvitationLocator(
                invitation.Id,
                invitation.OrganizationId))
            .SingleOrDefaultAsync(cancellationToken);

        if (locator is null)
        {
            return InvitedUserRegistrationPersistenceResult.InvalidInvitation;
        }

        try
        {
            await using var dbContext = new EnmaDbContext(dbContextOptions);
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);

            Organization? organization = await dbContext.Organizations
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM organizations
                    WHERE id = {locator.OrganizationId}
                    FOR UPDATE
                    """)
                .SingleOrDefaultAsync(cancellationToken);
            OrganizationInvitation? invitation =
                await dbContext.OrganizationInvitations
                    .FromSqlInterpolated(
                        $"""
                        SELECT * FROM organization_invitations
                        WHERE organization_id = {locator.OrganizationId}
                          AND id = {locator.InvitationId}
                        FOR UPDATE
                        """)
                    .SingleOrDefaultAsync(cancellationToken);
            DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();

            if (organization?.Id != locator.OrganizationId ||
                !organization.IsActive ||
                invitation?.Id != locator.InvitationId ||
                invitation.OrganizationId != locator.OrganizationId ||
                invitation.TokenHash is null ||
                !invitation.TokenHash.Equals(invitationTokenHash) ||
                invitation.GetState(now) != OrganizationInvitationState.Pending ||
                invitation.Role is not (
                    OrganizationRole.Administrator or OrganizationRole.Member))
            {
                await transaction.RollbackAsync(cancellationToken);
                return InvitedUserRegistrationPersistenceResult.InvalidInvitation;
            }

            if (!string.Equals(
                    invitation.InvitedEmail,
                    user.Email,
                    StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken);
                return InvitedUserRegistrationPersistenceResult.WrongRecipient;
            }

            if (await dbContext.Users.AsNoTracking().AnyAsync(
                    existingUser => existingUser.Email == user.Email,
                    cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return InvitedUserRegistrationPersistenceResult.ExistingUser;
            }

            dbContext.Users.Add(user);
            dbContext.UserCredentials.Add(credential);
            dbContext.EmailVerificationChallenges.Add(verificationChallenge);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return InvitedUserRegistrationPersistenceResult.Succeeded;
        }
        catch (DbUpdateException exception) when (
            IsUniqueViolation(exception, UserEmailConstraint))
        {
            return InvitedUserRegistrationPersistenceResult.ExistingUser;
        }
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

    private sealed record InvitationLocator(Guid InvitationId, Guid OrganizationId);
}
