using System.Data;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuthenticationSessionRevocationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string InitialPasswordHash = "synthetic-initial-password-hash";
    private const string ChangedPasswordHash = "synthetic-changed-password-hash";

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        11,
        16,
        30,
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
    public async Task RevokeAsync_WithExistingSession_RevokesWithoutRenewal()
    {
        SeededSession seeded = await SeedSessionAsync();

        await CreatePersistence().RevokeAsync(seeded.SecretHash, Now);

        AuthenticationSession persisted = await GetSessionAsync(seeded.Session.Id);
        Assert.Equal(Now, persisted.RevokedAt);
        Assert.Equal(seeded.Session.LastSeenAt, persisted.LastSeenAt);
        Assert.Equal(seeded.Session.IdleExpiresAt, persisted.IdleExpiresAt);
        Assert.Equal(seeded.Session.AbsoluteExpiresAt, persisted.AbsoluteExpiresAt);
        Assert.Equal(
            seeded.Session.ConcurrencyVersion + 1,
            persisted.ConcurrencyVersion);
    }

    [Fact]
    public async Task RevokeAsync_BeforeRuntimeValidation_RejectsWithoutIdleRenewal()
    {
        SeededSession seeded = await SeedSessionAsync();
        DateTimeOffset originalIdleExpiresAt = seeded.Session.IdleExpiresAt;

        await CreatePersistence().RevokeAsync(seeded.SecretHash, Now);
        Guid? result = await CreateRuntimePersistence().TryValidateAndRenewAsync(
            seeded.SecretHash,
            Now.AddMinutes(1));

        Assert.Null(result);
        AuthenticationSession persisted = await GetSessionAsync(seeded.Session.Id);
        Assert.Equal(Now, persisted.RevokedAt);
        Assert.Equal(originalIdleExpiresAt, persisted.IdleExpiresAt);
        Assert.Equal(seeded.Session.LastSeenAt, persisted.LastSeenAt);
    }

    [Fact]
    public async Task RevokeAsync_WithUnknownHash_DoesNotModifyAnySession()
    {
        SeededSession seeded = await SeedSessionAsync();

        await CreatePersistence().RevokeAsync(CreateSecretHash(101), Now);

        await AssertSessionStateAsync(seeded.Session);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.AuthenticationSessions.CountAsync());
    }

    [Fact]
    public async Task RevokeAsync_WithAlreadyRevokedSession_PreservesOriginalRevocation()
    {
        DateTimeOffset originalRevokedAt = Now.AddMinutes(-10);
        SeededSession seeded = await SeedSessionAsync(
            revokedAt: originalRevokedAt);

        await CreatePersistence().RevokeAsync(seeded.SecretHash, Now);

        await AssertSessionStateAsync(seeded.Session);
        AuthenticationSession persisted = await GetSessionAsync(seeded.Session.Id);
        Assert.Equal(originalRevokedAt, persisted.RevokedAt);
    }

    [Theory]
    [InlineData(RuntimeIneligibleSession.IdleExpired)]
    [InlineData(RuntimeIneligibleSession.AbsoluteExpired)]
    [InlineData(RuntimeIneligibleSession.StaleCredentialVersion)]
    [InlineData(RuntimeIneligibleSession.InactiveUser)]
    public async Task RevokeAsync_WithRuntimeIneligibleSession_StillRevokes(
        RuntimeIneligibleSession scenario)
    {
        DateTimeOffset idleExpiresAt = scenario switch
        {
            RuntimeIneligibleSession.IdleExpired => Now.AddMinutes(-1),
            RuntimeIneligibleSession.AbsoluteExpired => Now.AddMinutes(-2),
            _ => Now.AddMinutes(5)
        };
        DateTimeOffset absoluteExpiresAt = scenario ==
            RuntimeIneligibleSession.AbsoluteExpired
                ? Now.AddMinutes(-1)
                : Now.AddHours(2);
        SeededSession seeded = await SeedSessionAsync(
            userIsActive: scenario != RuntimeIneligibleSession.InactiveUser,
            currentCredentialVersion: scenario ==
                RuntimeIneligibleSession.StaleCredentialVersion ? 2 : 1,
            idleExpiresAt: idleExpiresAt,
            absoluteExpiresAt: absoluteExpiresAt);

        await CreatePersistence().RevokeAsync(seeded.SecretHash, Now);

        AuthenticationSession persisted = await GetSessionAsync(seeded.Session.Id);
        Assert.Equal(Now, persisted.RevokedAt);
        Assert.Equal(seeded.Session.LastSeenAt, persisted.LastSeenAt);
        Assert.Equal(seeded.Session.IdleExpiresAt, persisted.IdleExpiresAt);
        Assert.Equal(seeded.Session.AbsoluteExpiresAt, persisted.AbsoluteExpiresAt);
        Assert.Equal(
            seeded.Session.ConcurrencyVersion + 1,
            persisted.ConcurrencyVersion);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRevocationCommitsBeforeRuntimeSessionLock_RuntimeRejectsWithoutRenewal()
    {
        var handleService = new CryptographicAuthenticationSessionHandleService();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        SeededSession seeded = await SeedSessionAsync(secretHash: secretHash);
        DateTimeOffset originalIdleExpiresAt = seeded.Session.IdleExpiresAt;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using EnmaDbContext userLockContext = fixture.CreateDbContext();
        await using IDbContextTransaction userLockTransaction =
            await userLockContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await LockUserAsync(userLockContext, seeded.User.Id, timeout.Token);

        Task<Guid?>? validationTask = null;
        Task? revocationTask = null;
        bool userLockReleased = false;

        try
        {
            validationTask = CreateRuntimePersistence().TryValidateAndRenewAsync(
                secretHash,
                Now,
                timeout.Token);
            await WaitForBlockedCommandAsync("SELECT", "users", timeout.Token);

            var useCase = new RevokeSessionUseCase(
                handleService,
                CreatePersistence(),
                new FixedTimeProvider(Now));
            revocationTask = useCase.ExecuteAsync(rawHandle, timeout.Token);
            await revocationTask.WaitAsync(timeout.Token);

            AuthenticationSession revoked = await GetSessionAsync(seeded.Session.Id);
            Assert.Equal(Now, revoked.RevokedAt);

            await userLockTransaction.CommitAsync(timeout.Token);
            userLockReleased = true;

            Guid? result = await validationTask.WaitAsync(timeout.Token);

            Assert.Null(result);
            AuthenticationSession persisted = await GetSessionAsync(
                seeded.Session.Id);
            Assert.Equal(Now, persisted.RevokedAt);
            Assert.Equal(originalIdleExpiresAt, persisted.IdleExpiresAt);
            Assert.Equal(seeded.Session.LastSeenAt, persisted.LastSeenAt);
        }
        finally
        {
            if (!userLockReleased)
            {
                await userLockTransaction.RollbackAsync(CancellationToken.None);
            }

            timeout.Cancel();
            await DrainTaskAsync(revocationTask);
            await DrainTaskAsync(validationTask);
        }
    }

    private AuthenticationSessionRevocationPersistence CreatePersistence()
    {
        return new AuthenticationSessionRevocationPersistence(CreateOptions());
    }

    private AuthenticationSessionRuntimePersistence CreateRuntimePersistence()
    {
        return new AuthenticationSessionRuntimePersistence(CreateOptions());
    }

    private DbContextOptions<EnmaDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
    }

    private async Task<SeededSession> SeedSessionAsync(
        bool userIsActive = true,
        int currentCredentialVersion = 1,
        DateTimeOffset? idleExpiresAt = null,
        DateTimeOffset? absoluteExpiresAt = null,
        DateTimeOffset? revokedAt = null,
        AuthenticationSessionSecretHash? secretHash = null)
    {
        var user = new User(
            "Session Revocation User",
            "session-revocation@example.test",
            Now.AddHours(-6));
        user.VerifyEmail(Now.AddHours(-5));

        if (!userIsActive)
        {
            user.Deactivate();
        }

        var credential = new UserCredential(
            user.Id,
            InitialPasswordHash,
            Now.AddHours(-5));

        for (int version = 1; version < currentCredentialVersion; version++)
        {
            credential.ChangePasswordHash(
                ChangedPasswordHash + version,
                Now.AddHours(-4).AddMinutes(version));
        }

        AuthenticationSessionSecretHash sessionSecretHash = secretHash ??
            CreateSecretHash(1);
        var session = new AuthenticationSession(
            user.Id,
            sessionSecretHash,
            1,
            Now.AddHours(-3),
            idleExpiresAt ?? Now.AddMinutes(5),
            absoluteExpiresAt ?? Now.AddHours(2));

        if (revokedAt.HasValue)
        {
            session.Revoke(revokedAt.Value);
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Users.Add(user);
        dbContext.UserCredentials.Add(credential);
        dbContext.AuthenticationSessions.Add(session);
        await dbContext.SaveChangesAsync();

        return new SeededSession(user, session, sessionSecretHash);
    }

    private async Task<AuthenticationSession> GetSessionAsync(Guid sessionId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuthenticationSessions
            .AsNoTracking()
            .SingleAsync(session => session.Id == sessionId);
    }

    private async Task AssertSessionStateAsync(AuthenticationSession expected)
    {
        AuthenticationSession persisted = await GetSessionAsync(expected.Id);
        Assert.Equal(expected.UserId, persisted.UserId);
        Assert.Equal(expected.SecretHash, persisted.SecretHash);
        Assert.Equal(
            expected.CredentialVersionAtIssue,
            persisted.CredentialVersionAtIssue);
        Assert.Equal(expected.SelectedOrganizationId, persisted.SelectedOrganizationId);
        Assert.Equal(expected.CreatedAt, persisted.CreatedAt);
        Assert.Equal(expected.LastSeenAt, persisted.LastSeenAt);
        Assert.Equal(expected.IdleExpiresAt, persisted.IdleExpiresAt);
        Assert.Equal(expected.AbsoluteExpiresAt, persisted.AbsoluteExpiresAt);
        Assert.Equal(expected.RevokedAt, persisted.RevokedAt);
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

    private static async Task DrainTaskAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static AuthenticationSessionSecretHash CreateSecretHash(byte seed)
    {
        return new AuthenticationSessionSecretHash(
            Enumerable.Range(seed, 32)
                .Select(value => (byte)value)
                .ToArray());
    }

    public enum RuntimeIneligibleSession
    {
        IdleExpired,
        AbsoluteExpired,
        StaleCredentialVersion,
        InactiveUser
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    private sealed record SeededSession(
        User User,
        AuthenticationSession Session,
        AuthenticationSessionSecretHash SecretHash);
}
