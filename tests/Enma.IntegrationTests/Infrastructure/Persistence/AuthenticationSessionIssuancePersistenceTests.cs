using System.Data;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuthenticationSessionIssuancePersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string InitialSyntheticPasswordHash =
        "synthetic-opaque-issuance-hash-initial";
    private const string ChangedSyntheticPasswordHash =
        "synthetic-opaque-issuance-hash-changed";
    private const string UpgradedSyntheticPasswordHash =
        "synthetic-opaque-issuance-hash-upgraded";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        7,
        10,
        20,
        30,
        TimeSpan.Zero);

    private static readonly DateTimeOffset EmailVerifiedAt =
        CreatedAt.AddMinutes(5);

    private static readonly DateTimeOffset PasswordChangedAt =
        CreatedAt.AddHours(1);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TryPersistAsync_WithCurrentEligibleUserAndCredential_PersistsSession()
    {
        (User user, UserCredential credential) =
            await SeedUserWithCredentialAsync(emailVerified: true);
        AuthenticationSession session = CreateSession(user.Id, 1, 11);
        var persistence = CreatePersistence();

        AuthenticationSessionIssuancePersistenceResult result =
            await persistence.TryPersistAsync(session, null);

        Assert.Equal(
            AuthenticationSessionIssuancePersistenceResult.Succeeded,
            result);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        AuthenticationSession persistedSession = await verificationContext
            .AuthenticationSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == session.Id);
        UserCredential persistedCredential = await verificationContext.UserCredentials
            .AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == user.Id);

        Assert.Equal(session.Id, persistedSession.Id);
        Assert.Equal(session.UserId, persistedSession.UserId);
        Assert.Equal(session.SecretHash, persistedSession.SecretHash);
        Assert.Equal(
            session.CredentialVersionAtIssue,
            persistedSession.CredentialVersionAtIssue);
        Assert.Equal(session.SelectedOrganizationId, persistedSession.SelectedOrganizationId);
        Assert.Equal(session.CreatedAt, persistedSession.CreatedAt);
        Assert.Equal(session.LastSeenAt, persistedSession.LastSeenAt);
        Assert.Equal(session.IdleExpiresAt, persistedSession.IdleExpiresAt);
        Assert.Equal(session.AbsoluteExpiresAt, persistedSession.AbsoluteExpiresAt);
        Assert.Equal(session.RevokedAt, persistedSession.RevokedAt);
        Assert.Equal(session.ConcurrencyVersion, persistedSession.ConcurrencyVersion);
        AssertCredentialUnchanged(credential, persistedCredential);
    }

    [Fact]
    public async Task TryPersistAsync_WithInactiveUser_ReturnsRejectedWithoutWrites()
    {
        (User user, UserCredential credential) =
            await SeedUserWithCredentialAsync(emailVerified: true);

        await using (EnmaDbContext updateContext = fixture.CreateDbContext())
        {
            User persistedUser = await updateContext.Users.SingleAsync(
                candidate => candidate.Id == user.Id);
            persistedUser.Deactivate();
            await updateContext.SaveChangesAsync();
        }

        AuthenticationSession session = CreateSession(user.Id, 1, 21);

        AuthenticationSessionIssuancePersistenceResult result =
            await CreatePersistence().TryPersistAsync(session, null);

        Assert.Equal(
            AuthenticationSessionIssuancePersistenceResult.Rejected,
            result);
        await AssertNoSessionAndCredentialUnchangedAsync(session.Id, credential);
    }

    [Fact]
    public async Task TryPersistAsync_WithUnverifiedUser_ReturnsRejectedWithoutWrites()
    {
        (User user, UserCredential credential) =
            await SeedUserWithCredentialAsync(emailVerified: false);
        AuthenticationSession session = CreateSession(user.Id, 1, 31);

        AuthenticationSessionIssuancePersistenceResult result =
            await CreatePersistence().TryPersistAsync(session, null);

        Assert.Equal(
            AuthenticationSessionIssuancePersistenceResult.Rejected,
            result);
        await AssertNoSessionAndCredentialUnchangedAsync(session.Id, credential);
    }

    [Fact]
    public async Task TryPersistAsync_WithMissingCredential_ReturnsRejectedWithoutSession()
    {
        User user = await SeedUserWithoutCredentialAsync(emailVerified: true);
        AuthenticationSession session = CreateSession(user.Id, 1, 41);

        AuthenticationSessionIssuancePersistenceResult result =
            await CreatePersistence().TryPersistAsync(session, null);

        Assert.Equal(
            AuthenticationSessionIssuancePersistenceResult.Rejected,
            result);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.False(await verificationContext.AuthenticationSessions
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == session.Id));
        Assert.False(await verificationContext.UserCredentials
            .AsNoTracking()
            .AnyAsync(candidate => candidate.UserId == user.Id));
    }

    [Fact]
    public async Task TryPersistAsync_WithCredentialVersionMismatch_ReturnsRejectedWithoutWrites()
    {
        (User user, _) = await SeedUserWithCredentialAsync(emailVerified: true);

        await using (EnmaDbContext updateContext = fixture.CreateDbContext())
        {
            UserCredential persistedCredential = await updateContext.UserCredentials
                .SingleAsync(candidate => candidate.UserId == user.Id);
            persistedCredential.ChangePasswordHash(
                ChangedSyntheticPasswordHash,
                PasswordChangedAt);
            await updateContext.SaveChangesAsync();
        }

        AuthenticationSession session = CreateSession(user.Id, 1, 51);

        AuthenticationSessionIssuancePersistenceResult result =
            await CreatePersistence().TryPersistAsync(session, null);

        Assert.Equal(
            AuthenticationSessionIssuancePersistenceResult.Rejected,
            result);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.False(await verificationContext.AuthenticationSessions
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == session.Id));
        UserCredential currentCredential = await verificationContext.UserCredentials
            .AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == user.Id);
        Assert.Equal(ChangedSyntheticPasswordHash, currentCredential.PasswordHash);
        Assert.Equal(PasswordChangedAt, currentCredential.PasswordChangedAt);
        Assert.Equal(2, currentCredential.CredentialVersion);
    }

    [Fact]
    public async Task TryPersistAsync_WithTransparentRehash_PersistsRehashAndSessionAtomically()
    {
        (User user, UserCredential credential) =
            await SeedUserWithCredentialAsync(emailVerified: true);
        AuthenticationSession session = CreateSession(user.Id, 1, 61);

        AuthenticationSessionIssuancePersistenceResult result =
            await CreatePersistence().TryPersistAsync(
                session,
                UpgradedSyntheticPasswordHash);

        Assert.Equal(
            AuthenticationSessionIssuancePersistenceResult.Succeeded,
            result);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.True(await verificationContext.AuthenticationSessions
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == session.Id));
        UserCredential persistedCredential = await verificationContext.UserCredentials
            .AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == user.Id);
        Assert.Equal(
            UpgradedSyntheticPasswordHash,
            persistedCredential.PasswordHash);
        Assert.Equal(
            credential.PasswordChangedAt,
            persistedCredential.PasswordChangedAt);
        Assert.Equal(
            credential.CredentialVersion,
            persistedCredential.CredentialVersion);
    }

    [Fact]
    public async Task TryPersistAsync_WhenSessionInsertFails_RollsBackTransparentRehash()
    {
        (User user, UserCredential credential) =
            await SeedUserWithCredentialAsync(emailVerified: true);
        AuthenticationSession existingSession = CreateSession(user.Id, 1, 71);

        await using (EnmaDbContext setupContext = fixture.CreateDbContext())
        {
            setupContext.AuthenticationSessions.Add(existingSession);
            await setupContext.SaveChangesAsync();
        }

        var duplicateSession = new AuthenticationSession(
            user.Id,
            new AuthenticationSessionSecretHash(
                existingSession.SecretHash.ToArray()),
            1,
            CreatedAt.AddMinutes(1),
            CreatedAt.AddMinutes(31),
            CreatedAt.AddHours(2));

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => CreatePersistence().TryPersistAsync(
                duplicateSession,
                UpgradedSyntheticPasswordHash));
        PostgresException postgresException =
            Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal(
            "ux_authentication_sessions_secret_hash",
            postgresException.ConstraintName);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        UserCredential persistedCredential = await verificationContext.UserCredentials
            .AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == user.Id);
        AssertCredentialUnchanged(credential, persistedCredential);
        AuthenticationSession persistedSession = await verificationContext
            .AuthenticationSessions
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(existingSession.Id, persistedSession.Id);
        Assert.NotEqual(duplicateSession.Id, persistedSession.Id);
    }

    [Fact]
    public async Task TryPersistAsync_WithStaleCallerTrackedCredential_UsesFreshDatabaseState()
    {
        (User user, _) = await SeedUserWithCredentialAsync(emailVerified: true);
        await using EnmaDbContext callerContext = fixture.CreateDbContext();
        UserCredential staleCredential = await callerContext.UserCredentials
            .SingleAsync(candidate => candidate.UserId == user.Id);

        await using (EnmaDbContext updateContext = fixture.CreateDbContext())
        {
            UserCredential currentCredential = await updateContext.UserCredentials
                .SingleAsync(candidate => candidate.UserId == user.Id);
            currentCredential.ChangePasswordHash(
                ChangedSyntheticPasswordHash,
                PasswordChangedAt);
            await updateContext.SaveChangesAsync();
        }

        Assert.Equal(1, staleCredential.CredentialVersion);
        AuthenticationSession session = CreateSession(user.Id, 1, 81);

        AuthenticationSessionIssuancePersistenceResult result =
            await CreatePersistence().TryPersistAsync(session, null);

        Assert.Equal(
            AuthenticationSessionIssuancePersistenceResult.Rejected,
            result);
        Assert.Single(callerContext.ChangeTracker.Entries<UserCredential>());
        await AssertNoSessionAsync(session.Id);
    }

    [Fact]
    public async Task TryPersistAsync_WhenPasswordChangeCommitsBeforeCredentialLock_RejectsStaleSession()
    {
        (User user, _) = await SeedUserWithCredentialAsync(emailVerified: true);
        AuthenticationSession session = CreateSession(user.Id, 1, 91);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using EnmaDbContext userLockContext = fixture.CreateDbContext();
        await using var userLockTransaction =
            await userLockContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await LockUserAsync(userLockContext, user.Id, timeout.Token);

        Task<AuthenticationSessionIssuancePersistenceResult> issuanceTask =
            CreatePersistence().TryPersistAsync(session, null, timeout.Token);
        await WaitForBlockedCommandAsync(
            "SELECT",
            "users",
            timeout.Token);

        await using (EnmaDbContext passwordChangeContext = fixture.CreateDbContext())
        {
            UserCredential credential = await passwordChangeContext.UserCredentials
                .SingleAsync(
                    candidate => candidate.UserId == user.Id,
                    timeout.Token);
            credential.ChangePasswordHash(
                ChangedSyntheticPasswordHash,
                PasswordChangedAt);
            await passwordChangeContext.SaveChangesAsync(timeout.Token);
        }

        await userLockTransaction.CommitAsync(timeout.Token);
        AuthenticationSessionIssuancePersistenceResult result =
            await issuanceTask.WaitAsync(timeout.Token);

        Assert.Equal(
            AuthenticationSessionIssuancePersistenceResult.Rejected,
            result);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.False(await verificationContext.AuthenticationSessions
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == session.Id));
        UserCredential persistedCredential = await verificationContext.UserCredentials
            .AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == user.Id);
        Assert.Equal(2, persistedCredential.CredentialVersion);
        Assert.Equal(ChangedSyntheticPasswordHash, persistedCredential.PasswordHash);
    }

    [Fact]
    public async Task TryPersistAsync_WhenIssuanceLocksCredentialFirst_PasswordChangeCannotInvalidateBeforeCommit()
    {
        (User user, _) = await SeedUserWithCredentialAsync(emailVerified: true);
        var organization = new Organization(
            "Issuance Lock Organization",
            "issuance-lock-organization",
            CreatedAt);

        await using (EnmaDbContext setupContext = fixture.CreateDbContext())
        {
            setupContext.Organizations.Add(organization);
            await setupContext.SaveChangesAsync();
        }

        AuthenticationSession session = CreateSession(
            user.Id,
            1,
            101,
            organization.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using EnmaDbContext passwordChangeContext = fixture.CreateDbContext();
        UserCredential competingCredential = await passwordChangeContext.UserCredentials
            .SingleAsync(
                candidate => candidate.UserId == user.Id,
                timeout.Token);

        await using EnmaDbContext organizationLockContext = fixture.CreateDbContext();
        await using var organizationLockTransaction =
            await organizationLockContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await LockOrganizationAsync(
            organizationLockContext,
            organization.Id,
            timeout.Token);

        Task<AuthenticationSessionIssuancePersistenceResult> issuanceTask =
            CreatePersistence().TryPersistAsync(session, null, timeout.Token);
        await WaitForBlockedCommandAsync(
            "INSERT",
            "authentication_sessions",
            timeout.Token);

        competingCredential.ChangePasswordHash(
            ChangedSyntheticPasswordHash,
            PasswordChangedAt);
        Task<int> passwordChangeTask =
            passwordChangeContext.SaveChangesAsync(timeout.Token);
        await WaitForBlockedCommandAsync(
            "UPDATE",
            "user_credentials",
            timeout.Token);
        Assert.False(passwordChangeTask.IsCompleted);

        await organizationLockTransaction.CommitAsync(timeout.Token);
        AuthenticationSessionIssuancePersistenceResult result =
            await issuanceTask.WaitAsync(timeout.Token);
        await passwordChangeTask.WaitAsync(timeout.Token);

        Assert.Equal(
            AuthenticationSessionIssuancePersistenceResult.Succeeded,
            result);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        AuthenticationSession persistedSession = await verificationContext
            .AuthenticationSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == session.Id);
        UserCredential persistedCredential = await verificationContext.UserCredentials
            .AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == user.Id);
        Assert.Equal(1, persistedSession.CredentialVersionAtIssue);
        Assert.Equal(2, persistedCredential.CredentialVersion);
        Assert.NotEqual(
            persistedCredential.CredentialVersion,
            persistedSession.CredentialVersionAtIssue);
    }

    [Fact]
    public async Task TryPersistAsync_RevalidatesCurrentUserEligibilityInsideTransaction()
    {
        (User user, _) = await SeedUserWithCredentialAsync(emailVerified: true);
        await using EnmaDbContext callerContext = fixture.CreateDbContext();
        User staleUser = await callerContext.Users.SingleAsync(
            candidate => candidate.Id == user.Id);

        await using (EnmaDbContext updateContext = fixture.CreateDbContext())
        {
            User currentUser = await updateContext.Users.SingleAsync(
                candidate => candidate.Id == user.Id);
            currentUser.Deactivate();
            await updateContext.SaveChangesAsync();
        }

        Assert.True(staleUser.IsActive);
        AuthenticationSession session = CreateSession(user.Id, 1, 111);

        AuthenticationSessionIssuancePersistenceResult result =
            await CreatePersistence().TryPersistAsync(session, null);

        Assert.Equal(
            AuthenticationSessionIssuancePersistenceResult.Rejected,
            result);
        Assert.Single(callerContext.ChangeTracker.Entries<User>());
        await AssertNoSessionAsync(session.Id);
    }

    private AuthenticationSessionIssuancePersistence CreatePersistence()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;

        return new AuthenticationSessionIssuancePersistence(options);
    }

    private async Task<(User User, UserCredential Credential)>
        SeedUserWithCredentialAsync(bool emailVerified)
    {
        User user = CreateUser();

        if (emailVerified)
        {
            user.VerifyEmail(EmailVerifiedAt);
        }

        var credential = new UserCredential(
            user.Id,
            InitialSyntheticPasswordHash,
            CreatedAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Users.Add(user);
        dbContext.UserCredentials.Add(credential);
        await dbContext.SaveChangesAsync();

        return (user, credential);
    }

    private async Task<User> SeedUserWithoutCredentialAsync(bool emailVerified)
    {
        User user = CreateUser();

        if (emailVerified)
        {
            user.VerifyEmail(EmailVerifiedAt);
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        return user;
    }

    private async Task AssertNoSessionAndCredentialUnchangedAsync(
        Guid sessionId,
        UserCredential expectedCredential)
    {
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.False(await verificationContext.AuthenticationSessions
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == sessionId));
        UserCredential persistedCredential = await verificationContext.UserCredentials
            .AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == expectedCredential.UserId);
        AssertCredentialUnchanged(expectedCredential, persistedCredential);
    }

    private async Task AssertNoSessionAsync(Guid sessionId)
    {
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.False(await verificationContext.AuthenticationSessions
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == sessionId));
    }

    private async Task WaitForBlockedCommandAsync(
        string command,
        string relation,
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext observationContext = fixture.CreateDbContext();
        string commandPattern = $"%{command}%";
        string relationPattern = $"%{relation}%";

        while (true)
        {
            int waitingCommandCount = await observationContext.Database
                .SqlQuery<int>(
                    $"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND wait_event_type = 'Lock'
                      AND query ILIKE {commandPattern}
                      AND query ILIKE {relationPattern}
                    """)
                .SingleAsync(cancellationToken);

            if (waitingCommandCount > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    private static async Task LockUserAsync(
        EnmaDbContext dbContext,
        Guid userId,
        CancellationToken cancellationToken)
    {
        User[] users = await dbContext.Users
            .FromSqlInterpolated(
                $"SELECT * FROM users WHERE id = {userId} FOR UPDATE")
            .ToArrayAsync(cancellationToken);
        Assert.Single(users);
    }

    private static async Task LockOrganizationAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        Organization[] organizations = await dbContext.Organizations
            .FromSqlInterpolated(
                $"SELECT * FROM organizations WHERE id = {organizationId} FOR UPDATE")
            .ToArrayAsync(cancellationToken);
        Assert.Single(organizations);
    }

    private static void AssertCredentialUnchanged(
        UserCredential expected,
        UserCredential actual)
    {
        Assert.Equal(expected.UserId, actual.UserId);
        Assert.Equal(expected.PasswordHash, actual.PasswordHash);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.PasswordChangedAt, actual.PasswordChangedAt);
        Assert.Equal(expected.CredentialVersion, actual.CredentialVersion);
    }

    private static AuthenticationSession CreateSession(
        Guid userId,
        long credentialVersionAtIssue,
        byte hashSeed,
        Guid? selectedOrganizationId = null)
    {
        return new AuthenticationSession(
            userId,
            CreateSecretHash(hashSeed),
            credentialVersionAtIssue,
            CreatedAt,
            CreatedAt.AddMinutes(30),
            CreatedAt.AddHours(2),
            selectedOrganizationId);
    }

    private static AuthenticationSessionSecretHash CreateSecretHash(byte seed)
    {
        byte[] value = Enumerable.Range(seed, 32)
            .Select(number => (byte)number)
            .ToArray();

        return new AuthenticationSessionSecretHash(value);
    }

    private static User CreateUser()
    {
        return new User(
            "Authentication Session Issuance User",
            "authentication-session-issuance@example.test",
            CreatedAt);
    }
}
