using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuthenticationSessionPersistenceTests(PostgreSqlFixture fixture)
    : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        4,
        5,
        6,
        7,
        8,
        TimeSpan.Zero);
    private static readonly DateTimeOffset IdleExpiresAt = CreatedAt.AddMinutes(30);
    private static readonly DateTimeOffset AbsoluteExpiresAt = CreatedAt.AddHours(2);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SaveChanges_WithValidSession_PersistsCompleteSession()
    {
        User user = CreateUser("complete-session@example.com");
        byte[] expectedHash = CreateHashBytes(11);
        var session = new AuthenticationSession(
            user.Id,
            new AuthenticationSessionSecretHash(expectedHash),
            4,
            CreatedAt,
            IdleExpiresAt,
            AbsoluteExpiresAt);
        DateTimeOffset seenAt = CreatedAt.AddMinutes(10);
        DateTimeOffset renewedIdleExpiresAt = IdleExpiresAt.AddMinutes(15);
        DateTimeOffset revokedAt = AbsoluteExpiresAt.AddHours(1);
        session.Touch(seenAt, renewedIdleExpiresAt);
        session.Revoke(revokedAt);

        await using (EnmaDbContext setupContext = fixture.CreateDbContext())
        {
            setupContext.Users.Add(user);
            setupContext.AuthenticationSessions.Add(session);
            await setupContext.SaveChangesAsync();
        }

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        AuthenticationSession persistedSession = await verificationContext
            .AuthenticationSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == session.Id);

        Assert.Equal(session.Id, persistedSession.Id);
        Assert.Equal(user.Id, persistedSession.UserId);
        Assert.True(expectedHash.SequenceEqual(persistedSession.SecretHash.ToArray()));
        Assert.Equal(4, persistedSession.CredentialVersionAtIssue);
        Assert.Equal(CreatedAt, persistedSession.CreatedAt);
        Assert.Equal(seenAt, persistedSession.LastSeenAt);
        Assert.Equal(renewedIdleExpiresAt, persistedSession.IdleExpiresAt);
        Assert.Equal(AbsoluteExpiresAt, persistedSession.AbsoluteExpiresAt);
        Assert.Equal(revokedAt, persistedSession.RevokedAt);
        Assert.Equal(3, persistedSession.ConcurrencyVersion);

        var configureConventionsMethod = typeof(EnmaDbContext).GetMethod(
            "ConfigureConventions",
            System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(configureConventionsMethod);
        Assert.Equal(typeof(DbContext), configureConventionsMethod.DeclaringType);
    }

    [Fact]
    public async Task SaveChanges_WithDuplicateSecretHash_RejectsUniqueConstraint()
    {
        User user = CreateUser("duplicate-session@example.com");
        byte[] hash = CreateHashBytes(21);
        var firstSession = new AuthenticationSession(
            user.Id,
            new AuthenticationSessionSecretHash(hash),
            1,
            CreatedAt,
            IdleExpiresAt,
            AbsoluteExpiresAt);
        var secondSession = new AuthenticationSession(
            user.Id,
            new AuthenticationSessionSecretHash((byte[])hash.Clone()),
            1,
            CreatedAt,
            IdleExpiresAt,
            AbsoluteExpiresAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Users.Add(user);
        dbContext.AuthenticationSessions.AddRange(firstSession, secondSession);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());
        PostgresException postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal(
            "ux_authentication_sessions_secret_hash",
            postgresException.ConstraintName);
    }

    [Fact]
    public async Task DatabaseConstraints_RejectInvalidSessionState()
    {
        User user = CreateUser("session-constraints@example.com");

        await using (EnmaDbContext setupContext = fixture.CreateDbContext())
        {
            setupContext.Users.Add(user);
            await setupContext.SaveChangesAsync();
        }

        await AssertCheckConstraintAsync(
            Guid.Parse("00000000-0000-0000-0000-000000000101"),
            user.Id,
            new byte[31],
            1,
            CreatedAt,
            CreatedAt,
            IdleExpiresAt,
            AbsoluteExpiresAt,
            CreatedAt,
            1,
            "ck_authentication_sessions_secret_hash_length");
        await AssertCheckConstraintAsync(
            Guid.Parse("00000000-0000-0000-0000-000000000102"),
            user.Id,
            CreateHashBytes(32),
            0,
            CreatedAt,
            CreatedAt,
            IdleExpiresAt,
            AbsoluteExpiresAt,
            CreatedAt,
            1,
            "ck_authentication_sessions_credential_version_at_issue");
        await AssertCheckConstraintAsync(
            Guid.Parse("00000000-0000-0000-0000-000000000103"),
            user.Id,
            CreateHashBytes(33),
            1,
            CreatedAt,
            CreatedAt.AddTicks(-1),
            IdleExpiresAt,
            AbsoluteExpiresAt,
            CreatedAt,
            1,
            "ck_authentication_sessions_last_seen_at");
        await AssertCheckConstraintAsync(
            Guid.Parse("00000000-0000-0000-0000-000000000104"),
            user.Id,
            CreateHashBytes(34),
            1,
            CreatedAt,
            CreatedAt,
            CreatedAt.AddMinutes(1),
            CreatedAt,
            CreatedAt,
            1,
            "ck_authentication_sessions_absolute_expires_at");
        await AssertCheckConstraintAsync(
            Guid.Parse("00000000-0000-0000-0000-000000000105"),
            user.Id,
            CreateHashBytes(35),
            1,
            CreatedAt,
            CreatedAt,
            CreatedAt,
            AbsoluteExpiresAt,
            CreatedAt,
            1,
            "ck_authentication_sessions_idle_expires_at");
        await AssertCheckConstraintAsync(
            Guid.Parse("00000000-0000-0000-0000-000000000106"),
            user.Id,
            CreateHashBytes(36),
            1,
            CreatedAt,
            CreatedAt,
            AbsoluteExpiresAt.AddMilliseconds(1),
            AbsoluteExpiresAt,
            CreatedAt,
            1,
            "ck_authentication_sessions_idle_expires_at");
        await AssertCheckConstraintAsync(
            Guid.Parse("00000000-0000-0000-0000-000000000107"),
            user.Id,
            CreateHashBytes(37),
            1,
            CreatedAt,
            CreatedAt,
            IdleExpiresAt,
            AbsoluteExpiresAt,
            CreatedAt.AddTicks(-1),
            1,
            "ck_authentication_sessions_revoked_at");
        await AssertCheckConstraintAsync(
            Guid.Parse("00000000-0000-0000-0000-000000000108"),
            user.Id,
            CreateHashBytes(38),
            1,
            CreatedAt,
            CreatedAt,
            IdleExpiresAt,
            AbsoluteExpiresAt,
            CreatedAt,
            0,
            "ck_authentication_sessions_concurrency_version");
    }

    [Fact]
    public async Task UserDeletion_CascadesAuthenticationSessions()
    {
        User user = CreateUser("cascade-session@example.com");
        AuthenticationSession session = CreateSession(user.Id, 41);

        await using (EnmaDbContext setupContext = fixture.CreateDbContext())
        {
            setupContext.Users.Add(user);
            setupContext.AuthenticationSessions.Add(session);
            await setupContext.SaveChangesAsync();
        }

        await using (EnmaDbContext deleteContext = fixture.CreateDbContext())
        {
            User persistedUser = await deleteContext.Users
                .SingleAsync(candidate => candidate.Id == user.Id);
            deleteContext.Users.Remove(persistedUser);
            await deleteContext.SaveChangesAsync();
        }

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.False(await verificationContext.AuthenticationSessions
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == session.Id));
    }

    [Fact]
    public void AuthenticationSessionModel_HasNoDurableOrganizationSelectionState()
    {
        using EnmaDbContext dbContext = fixture.CreateDbContext();
        var sessionEntityType = dbContext.Model.FindEntityType(
            typeof(AuthenticationSession));

        Assert.NotNull(sessionEntityType);
        Assert.Null(sessionEntityType.FindProperty("SelectedOrganizationId"));
        Assert.DoesNotContain(
            sessionEntityType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType ==
                typeof(Organization));
        Assert.DoesNotContain(
            sessionEntityType.GetIndexes(),
            index => index.GetDatabaseName() ==
                "ix_authentication_sessions_selected_organization_id");
        Assert.Null(typeof(AuthenticationSession).GetProperty(
            "SelectedOrganizationId"));
        Assert.Null(typeof(AuthenticationSession).GetMethod("SelectOrganization"));
        Assert.Null(typeof(AuthenticationSession).GetMethod(
            "ClearSelectedOrganization"));
    }

    [Fact]
    public async Task DomainRevocation_ThenSave_PersistsRevocationAndConcurrencyVersion()
    {
        User user = CreateUser("revoked-session@example.com");
        AuthenticationSession session = CreateSession(user.Id, 51);

        await SeedSessionAsync(user, session);

        DateTimeOffset revokedAt = AbsoluteExpiresAt.AddHours(1);

        await using (EnmaDbContext updateContext = fixture.CreateDbContext())
        {
            AuthenticationSession trackedSession = await updateContext
                .AuthenticationSessions
                .SingleAsync(candidate => candidate.Id == session.Id);
            trackedSession.Revoke(revokedAt);
            await updateContext.SaveChangesAsync();
        }

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        AuthenticationSession persistedSession = await verificationContext
            .AuthenticationSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == session.Id);

        Assert.Equal(revokedAt, persistedSession.RevokedAt);
        Assert.Equal(2, persistedSession.ConcurrencyVersion);
    }

    [Fact]
    public async Task DomainTouch_ThenSave_PersistsRenewalAndConcurrencyVersion()
    {
        User user = CreateUser("touched-session@example.com");
        AuthenticationSession session = CreateSession(user.Id, 52);

        await SeedSessionAsync(user, session);

        DateTimeOffset seenAt = CreatedAt.AddMinutes(10);
        DateTimeOffset renewedIdleExpiresAt = IdleExpiresAt.AddMinutes(20);

        await using (EnmaDbContext updateContext = fixture.CreateDbContext())
        {
            AuthenticationSession trackedSession = await updateContext
                .AuthenticationSessions
                .SingleAsync(candidate => candidate.Id == session.Id);
            trackedSession.Touch(seenAt, renewedIdleExpiresAt);
            await updateContext.SaveChangesAsync();
        }

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        AuthenticationSession persistedSession = await verificationContext
            .AuthenticationSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == session.Id);

        Assert.Equal(seenAt, persistedSession.LastSeenAt);
        Assert.Equal(renewedIdleExpiresAt, persistedSession.IdleExpiresAt);
        Assert.Equal(2, persistedSession.ConcurrencyVersion);
    }

    [Fact]
    public async Task ConcurrentSessionMutation_RejectsStaleUpdate()
    {
        User user = CreateUser("concurrent-session@example.com");
        AuthenticationSession session = CreateSession(user.Id, 53);

        await SeedSessionAsync(user, session);

        await using EnmaDbContext firstContext = fixture.CreateDbContext();
        await using EnmaDbContext secondContext = fixture.CreateDbContext();
        AuthenticationSession firstSession = await firstContext.AuthenticationSessions
            .SingleAsync(candidate => candidate.Id == session.Id);
        AuthenticationSession secondSession = await secondContext.AuthenticationSessions
            .SingleAsync(candidate => candidate.Id == session.Id);
        DateTimeOffset firstSeenAt = CreatedAt.AddMinutes(5);
        DateTimeOffset firstIdleExpiresAt = IdleExpiresAt.AddMinutes(10);
        DateTimeOffset staleSeenAt = CreatedAt.AddMinutes(10);
        DateTimeOffset staleIdleExpiresAt = IdleExpiresAt.AddMinutes(20);

        firstSession.Touch(firstSeenAt, firstIdleExpiresAt);
        secondSession.Touch(staleSeenAt, staleIdleExpiresAt);

        await firstContext.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => secondContext.SaveChangesAsync());

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        AuthenticationSession persistedSession = await verificationContext
            .AuthenticationSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == session.Id);

        Assert.Equal(firstSeenAt, persistedSession.LastSeenAt);
        Assert.Equal(firstIdleExpiresAt, persistedSession.IdleExpiresAt);
        Assert.Equal(2, persistedSession.ConcurrencyVersion);
    }

    private async Task AssertCheckConstraintAsync(
        Guid sessionId,
        Guid userId,
        byte[] secretHash,
        long credentialVersionAtIssue,
        DateTimeOffset createdAt,
        DateTimeOffset lastSeenAt,
        DateTimeOffset idleExpiresAt,
        DateTimeOffset absoluteExpiresAt,
        DateTimeOffset revokedAt,
        long concurrencyVersion,
        string expectedConstraintName)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            async () => await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO authentication_sessions
                    (id, user_id, secret_hash, credential_version_at_issue,
                     created_at, last_seen_at, idle_expires_at, absolute_expires_at,
                     revoked_at, concurrency_version)
                VALUES
                    ({sessionId}, {userId}, {secretHash}, {credentialVersionAtIssue},
                     {createdAt}, {lastSeenAt}, {idleExpiresAt}, {absoluteExpiresAt},
                     {revokedAt}, {concurrencyVersion})
                """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(expectedConstraintName, exception.ConstraintName);
    }

    private async Task SeedSessionAsync(
        User user,
        AuthenticationSession session)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Users.Add(user);
        dbContext.AuthenticationSessions.Add(session);
        await dbContext.SaveChangesAsync();
    }

    private static AuthenticationSession CreateSession(
        Guid userId,
        byte hashSeed)
    {
        return new AuthenticationSession(
            userId,
            new AuthenticationSessionSecretHash(CreateHashBytes(hashSeed)),
            1,
            CreatedAt,
            IdleExpiresAt,
            AbsoluteExpiresAt);
    }

    private static byte[] CreateHashBytes(byte seed)
    {
        return Enumerable.Range(0, 32)
            .Select(index => (byte)(seed + index))
            .ToArray();
    }

    private static User CreateUser(string email)
    {
        return new User("Authentication Session User", email, CreatedAt);
    }
}
