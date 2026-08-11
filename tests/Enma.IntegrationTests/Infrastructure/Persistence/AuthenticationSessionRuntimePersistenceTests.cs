using System.Data;
using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuthenticationSessionRuntimePersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string InitialPasswordHash = "synthetic-initial-password-hash";
    private const string ChangedPasswordHash = "synthetic-changed-password-hash";

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        11,
        15,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TryValidateAndRenewAsync_WithValidSession_AuthenticatesAndRenewsIdleExpiration()
    {
        SeededSession seeded = await SeedSessionAsync();

        Guid? result = await CreatePersistence().TryValidateAndRenewAsync(
            seeded.SecretHash,
            Now);

        Assert.Equal(seeded.User.Id, result);
        AuthenticationSession persisted = await GetSessionAsync(seeded.Session.Id);
        Assert.Equal(Now, persisted.LastSeenAt);
        Assert.Equal(Now.AddMinutes(30), persisted.IdleExpiresAt);
        Assert.Equal(2, persisted.ConcurrencyVersion);
    }

    [Fact]
    public async Task TryValidateAndRenewAsync_WithUnknownSecretHash_ReturnsInvalid()
    {
        SeededSession seeded = await SeedSessionAsync();

        Guid? result = await CreatePersistence().TryValidateAndRenewAsync(
            CreateSecretHash(101),
            Now);

        Assert.Null(result);
        await AssertSessionUnchangedAsync(seeded.Session);
    }

    [Fact]
    public async Task TryValidateAndRenewAsync_WithRevokedSession_ReturnsInvalidWithoutRenewal()
    {
        SeededSession seeded = await SeedSessionAsync(revokedAt: Now.AddMinutes(-1));

        Guid? result = await CreatePersistence().TryValidateAndRenewAsync(
            seeded.SecretHash,
            Now);

        Assert.Null(result);
        await AssertSessionUnchangedAsync(seeded.Session);
    }

    [Fact]
    public async Task TryValidateAndRenewAsync_AtIdleExpiration_ReturnsInvalidWithoutRenewal()
    {
        SeededSession seeded = await SeedSessionAsync(idleExpiresAt: Now);

        Guid? result = await CreatePersistence().TryValidateAndRenewAsync(
            seeded.SecretHash,
            Now);

        Assert.Null(result);
        await AssertSessionUnchangedAsync(seeded.Session);
    }

    [Fact]
    public async Task TryValidateAndRenewAsync_AfterIdleExpiration_ReturnsInvalidWithoutRenewal()
    {
        SeededSession seeded = await SeedSessionAsync(
            idleExpiresAt: Now.AddMilliseconds(-1));

        Guid? result = await CreatePersistence().TryValidateAndRenewAsync(
            seeded.SecretHash,
            Now);

        Assert.Null(result);
        await AssertSessionUnchangedAsync(seeded.Session);
    }

    [Fact]
    public async Task TryValidateAndRenewAsync_AtAbsoluteExpiration_ReturnsInvalidWithoutRenewal()
    {
        SeededSession seeded = await SeedSessionAsync(
            idleExpiresAt: Now,
            absoluteExpiresAt: Now);

        Guid? result = await CreatePersistence().TryValidateAndRenewAsync(
            seeded.SecretHash,
            Now);

        Assert.Null(result);
        await AssertSessionUnchangedAsync(seeded.Session);
    }

    [Fact]
    public async Task TryValidateAndRenewAsync_AfterAbsoluteExpiration_ReturnsInvalidWithoutRenewal()
    {
        SeededSession seeded = await SeedSessionAsync(
            idleExpiresAt: Now.AddMinutes(-1),
            absoluteExpiresAt: Now.AddMilliseconds(-1));

        Guid? result = await CreatePersistence().TryValidateAndRenewAsync(
            seeded.SecretHash,
            Now);

        Assert.Null(result);
        await AssertSessionUnchangedAsync(seeded.Session);
    }

    [Fact]
    public async Task TryValidateAndRenewAsync_WithInactiveUser_ReturnsInvalidWithoutRenewal()
    {
        SeededSession seeded = await SeedSessionAsync(userIsActive: false);

        Guid? result = await CreatePersistence().TryValidateAndRenewAsync(
            seeded.SecretHash,
            Now);

        Assert.Null(result);
        await AssertSessionUnchangedAsync(seeded.Session);
    }

    [Fact]
    public async Task TryValidateAndRenewAsync_WithMissingCurrentCredential_ReturnsInvalidWithoutRenewal()
    {
        SeededSession seeded = await SeedSessionAsync(includeCredential: false);

        Guid? result = await CreatePersistence().TryValidateAndRenewAsync(
            seeded.SecretHash,
            Now);

        Assert.Null(result);
        await AssertSessionUnchangedAsync(seeded.Session);
    }

    [Fact]
    public async Task TryValidateAndRenewAsync_WithCredentialVersionMismatch_ReturnsInvalidWithoutRenewal()
    {
        SeededSession seeded = await SeedSessionAsync(
            currentCredentialVersion: 2,
            credentialVersionAtIssue: 1);

        Guid? result = await CreatePersistence().TryValidateAndRenewAsync(
            seeded.SecretHash,
            Now);

        Assert.Null(result);
        await AssertSessionUnchangedAsync(seeded.Session);
    }

    [Fact]
    public async Task TryValidateAndRenewAsync_WithCurrentCredentialVersion_Authenticates()
    {
        SeededSession seeded = await SeedSessionAsync(
            currentCredentialVersion: 2,
            credentialVersionAtIssue: 2);

        Guid? result = await CreatePersistence().TryValidateAndRenewAsync(
            seeded.SecretHash,
            Now);

        Assert.Equal(seeded.User.Id, result);
    }

    [Fact]
    public async Task TryValidateAndRenewAsync_WhenCandidateExceedsAbsoluteExpiration_CapsRenewal()
    {
        DateTimeOffset absoluteExpiresAt = Now.AddMinutes(10);
        SeededSession seeded = await SeedSessionAsync(
            idleExpiresAt: Now.AddMinutes(5),
            absoluteExpiresAt: absoluteExpiresAt);

        Guid? result = await CreatePersistence().TryValidateAndRenewAsync(
            seeded.SecretHash,
            Now);

        Assert.Equal(seeded.User.Id, result);
        AuthenticationSession persisted = await GetSessionAsync(seeded.Session.Id);
        Assert.Equal(absoluteExpiresAt, persisted.IdleExpiresAt);
    }

    [Fact]
    public async Task TryValidateAndRenewAsync_WhenClockMovesBackward_DoesNotShortenSession()
    {
        SeededSession seeded = await SeedSessionAsync(
            idleExpiresAt: Now.AddMinutes(10));

        await using (EnmaDbContext updateContext = fixture.CreateDbContext())
        {
            AuthenticationSession session = await updateContext.AuthenticationSessions
                .SingleAsync(candidate => candidate.Id == seeded.Session.Id);
            session.Touch(
                Now.AddMinutes(5),
                Now.AddMinutes(45));
            await updateContext.SaveChangesAsync();
        }

        const long expectedConcurrencyVersion = 2;

        Guid? result = await CreatePersistence().TryValidateAndRenewAsync(
            seeded.SecretHash,
            Now);

        Assert.Equal(seeded.User.Id, result);
        AuthenticationSession persisted = await GetSessionAsync(seeded.Session.Id);
        Assert.Equal(Now.AddMinutes(5), persisted.LastSeenAt);
        Assert.Equal(Now.AddMinutes(45), persisted.IdleExpiresAt);
        Assert.Equal(expectedConcurrencyVersion, persisted.ConcurrencyVersion);
    }

    [Fact]
    public async Task TryValidateAndRenewAsync_WhenCredentialChangeCommitsBeforeCredentialLock_RejectsStaleSession()
    {
        SeededSession seeded = await SeedSessionAsync();
        DateTimeOffset originalIdleExpiresAt = seeded.Session.IdleExpiresAt;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using EnmaDbContext userLockContext = fixture.CreateDbContext();
        await using IDbContextTransaction userLockTransaction =
            await userLockContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await LockUserAsync(userLockContext, seeded.User.Id, timeout.Token);

        Task<Guid?> validationTask = CreatePersistence().TryValidateAndRenewAsync(
            seeded.SecretHash,
            Now,
            timeout.Token);
        bool userLockReleased = false;

        try
        {
            await WaitForBlockedCommandAsync("SELECT", "users", timeout.Token);

            await using (EnmaDbContext passwordChangeContext =
                fixture.CreateDbContext())
            {
                UserCredential credential = await passwordChangeContext.UserCredentials
                    .SingleAsync(
                        candidate => candidate.UserId == seeded.User.Id,
                        timeout.Token);
                credential.ChangePasswordHash(
                    ChangedPasswordHash,
                    Now.AddMinutes(-1));
                await passwordChangeContext.SaveChangesAsync(timeout.Token);
            }

            await userLockTransaction.CommitAsync(timeout.Token);
            userLockReleased = true;

            Guid? result = await validationTask.WaitAsync(timeout.Token);

            Assert.Null(result);
            AuthenticationSession persisted = await GetSessionAsync(
                seeded.Session.Id);
            Assert.Equal(originalIdleExpiresAt, persisted.IdleExpiresAt);

            await using EnmaDbContext verificationContext = fixture.CreateDbContext();
            UserCredential currentCredential = await verificationContext.UserCredentials
                .AsNoTracking()
                .SingleAsync(candidate => candidate.UserId == seeded.User.Id);
            Assert.Equal(2, currentCredential.CredentialVersion);
        }
        finally
        {
            if (!userLockReleased)
            {
                await userLockTransaction.RollbackAsync(CancellationToken.None);
            }

            if (!validationTask.IsCompleted)
            {
                timeout.Cancel();

                try
                {
                    await validationTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private AuthenticationSessionRuntimePersistence CreatePersistence()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;

        return new AuthenticationSessionRuntimePersistence(options);
    }

    private async Task<SeededSession> SeedSessionAsync(
        bool userIsActive = true,
        bool includeCredential = true,
        int currentCredentialVersion = 1,
        long credentialVersionAtIssue = 1,
        DateTimeOffset? idleExpiresAt = null,
        DateTimeOffset? absoluteExpiresAt = null,
        DateTimeOffset? revokedAt = null)
    {
        var user = new User(
            "Session Runtime User",
            "session-runtime@example.test",
            Now.AddHours(-4));
        user.VerifyEmail(Now.AddHours(-3));

        if (!userIsActive)
        {
            user.Deactivate();
        }

        var credential = new UserCredential(
            user.Id,
            InitialPasswordHash,
            Now.AddHours(-3));

        for (int version = 1; version < currentCredentialVersion; version++)
        {
            credential.ChangePasswordHash(
                ChangedPasswordHash + version,
                Now.AddHours(-2).AddMinutes(version));
        }

        AuthenticationSessionSecretHash secretHash = CreateSecretHash(1);
        var session = new AuthenticationSession(
            user.Id,
            secretHash,
            credentialVersionAtIssue,
            Now.AddHours(-1),
            idleExpiresAt ?? Now.AddMinutes(5),
            absoluteExpiresAt ?? Now.AddHours(2));

        if (revokedAt.HasValue)
        {
            session.Revoke(revokedAt.Value);
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Users.Add(user);

        if (includeCredential)
        {
            dbContext.UserCredentials.Add(credential);
        }

        dbContext.AuthenticationSessions.Add(session);
        await dbContext.SaveChangesAsync();

        return new SeededSession(user, session, secretHash);
    }

    private async Task<AuthenticationSession> GetSessionAsync(Guid sessionId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuthenticationSessions
            .AsNoTracking()
            .SingleAsync(session => session.Id == sessionId);
    }

    private async Task AssertSessionUnchangedAsync(AuthenticationSession expected)
    {
        AuthenticationSession persisted = await GetSessionAsync(expected.Id);
        Assert.Equal(expected.LastSeenAt, persisted.LastSeenAt);
        Assert.Equal(expected.IdleExpiresAt, persisted.IdleExpiresAt);
        Assert.Equal(expected.ConcurrencyVersion, persisted.ConcurrencyVersion);
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

    private static AuthenticationSessionSecretHash CreateSecretHash(byte seed)
    {
        return new AuthenticationSessionSecretHash(
            Enumerable.Range(seed, 32)
                .Select(value => (byte)value)
                .ToArray());
    }

    private sealed record SeededSession(
        User User,
        AuthenticationSession Session,
        AuthenticationSessionSecretHash SecretHash);
}
