using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class EmailVerificationChallengePersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly Guid FirstUserId = Guid.Parse(
        "00000000-0000-0000-0000-000000000201");
    private static readonly Guid SecondUserId = Guid.Parse(
        "00000000-0000-0000-0000-000000000202");
    private static readonly Guid MissingUserId = Guid.Parse(
        "00000000-0000-0000-0000-000000000299");
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        6,
        7,
        8,
        9,
        10,
        TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiresAt = CreatedAt.AddHours(2);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SaveChanges_WithValidChallenge_PersistsAndMaterializesAllState()
    {
        EmailVerificationTokenHash tokenHash = CreateTokenHash(11);
        var challenge = new EmailVerificationChallenge(
            FirstUserId,
            "  VERIFY-ONE@ENMA.TEST  ",
            tokenHash,
            CreatedAt,
            ExpiresAt);

        await using (EnmaDbContext setupContext = fixture.CreateDbContext())
        {
            AddUser(setupContext, FirstUserId, "verify-one@enma.test");
            setupContext.EmailVerificationChallenges.Add(challenge);
            await setupContext.SaveChangesAsync();
        }

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        EmailVerificationChallenge persistedChallenge = await verificationContext
            .EmailVerificationChallenges
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(FirstUserId, persistedChallenge.UserId);
        Assert.Equal("verify-one@enma.test", persistedChallenge.EmailAtIssue);
        Assert.Equal(tokenHash, persistedChallenge.TokenHash);
        Assert.IsType<EmailVerificationTokenHash>(persistedChallenge.TokenHash);
        Assert.Equal(CreatedAt, persistedChallenge.CreatedAt);
        Assert.Equal(ExpiresAt, persistedChallenge.ExpiresAt);
    }

    [Fact]
    public async Task SaveChanges_AfterTrackedRotate_PersistsRotatedState()
    {
        EmailVerificationChallenge challenge = CreateChallenge(FirstUserId, 21);

        await using (EnmaDbContext setupContext = fixture.CreateDbContext())
        {
            AddUser(setupContext, FirstUserId, "rotate-initial@enma.test");
            setupContext.EmailVerificationChallenges.Add(challenge);
            await setupContext.SaveChangesAsync();
        }

        EmailVerificationTokenHash rotatedTokenHash = CreateTokenHash(22);
        DateTimeOffset rotatedCreatedAt = CreatedAt.AddMinutes(15);
        DateTimeOffset rotatedExpiresAt = rotatedCreatedAt.AddHours(3);

        await using (EnmaDbContext updateContext = fixture.CreateDbContext())
        {
            EmailVerificationChallenge trackedChallenge = await updateContext
                .EmailVerificationChallenges
                .SingleAsync(candidate => candidate.UserId == FirstUserId);
            trackedChallenge.Rotate(
                "  ROTATED@ENMA.TEST  ",
                rotatedTokenHash,
                rotatedCreatedAt,
                rotatedExpiresAt);
            await updateContext.SaveChangesAsync();
        }

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        EmailVerificationChallenge persistedChallenge = await verificationContext
            .EmailVerificationChallenges
            .AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == FirstUserId);

        Assert.Equal("rotated@enma.test", persistedChallenge.EmailAtIssue);
        Assert.Equal(rotatedTokenHash, persistedChallenge.TokenHash);
        Assert.Equal(rotatedCreatedAt, persistedChallenge.CreatedAt);
        Assert.Equal(rotatedExpiresAt, persistedChallenge.ExpiresAt);
    }

    [Fact]
    public async Task SaveChanges_WithSecondChallengeForSameUser_ViolatesPrimaryKey()
    {
        await using (EnmaDbContext firstContext = fixture.CreateDbContext())
        {
            AddUser(firstContext, FirstUserId, "same-user@enma.test");
            firstContext.EmailVerificationChallenges.Add(
                CreateChallenge(FirstUserId, 31));
            await firstContext.SaveChangesAsync();
        }

        await using EnmaDbContext secondContext = fixture.CreateDbContext();
        secondContext.EmailVerificationChallenges.Add(
            CreateChallenge(FirstUserId, 32));

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => secondContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.UniqueViolation,
            "pk_email_verification_challenges");
    }

    [Fact]
    public async Task SaveChanges_WithDuplicateTokenHashAcrossUsers_ViolatesUniqueConstraint()
    {
        byte[] sharedHashBytes = CreateHashBytes(41);

        await using (EnmaDbContext firstContext = fixture.CreateDbContext())
        {
            AddUser(firstContext, FirstUserId, "hash-one@enma.test");
            AddUser(firstContext, SecondUserId, "hash-two@enma.test");
            firstContext.EmailVerificationChallenges.Add(
                new EmailVerificationChallenge(
                    FirstUserId,
                    "hash-one@enma.test",
                    new EmailVerificationTokenHash(sharedHashBytes),
                    CreatedAt,
                    ExpiresAt));
            await firstContext.SaveChangesAsync();
        }

        await using EnmaDbContext secondContext = fixture.CreateDbContext();
        secondContext.EmailVerificationChallenges.Add(
            new EmailVerificationChallenge(
                SecondUserId,
                "hash-two@enma.test",
                new EmailVerificationTokenHash((byte[])sharedHashBytes.Clone()),
                CreatedAt,
                ExpiresAt));

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => secondContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.UniqueViolation,
            "ux_email_verification_challenges_token_hash");
    }

    [Fact]
    public async Task Database_WithInvalidTokenHashLength_RejectsRow()
    {
        await SeedUserAsync(FirstUserId, "invalid-hash@enma.test");

        await AssertCheckConstraintAsync(
            FirstUserId,
            new byte[31],
            CreatedAt,
            ExpiresAt,
            "ck_email_verification_challenges_token_hash_length");
    }

    [Fact]
    public async Task Database_WithExpirationEqualToCreation_RejectsRow()
    {
        await SeedUserAsync(FirstUserId, "equal-expiration@enma.test");

        await AssertCheckConstraintAsync(
            FirstUserId,
            CreateHashBytes(51),
            CreatedAt,
            CreatedAt,
            "ck_email_verification_challenges_expiration");
    }

    [Fact]
    public async Task Database_WithExpirationBeforeCreation_RejectsRow()
    {
        await SeedUserAsync(FirstUserId, "past-expiration@enma.test");

        await AssertCheckConstraintAsync(
            FirstUserId,
            CreateHashBytes(52),
            CreatedAt,
            CreatedAt.AddMinutes(-1),
            "ck_email_verification_challenges_expiration");
    }

    [Fact]
    public async Task SaveChanges_WithMissingUser_ViolatesForeignKey()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.EmailVerificationChallenges.Add(
            CreateChallenge(MissingUserId, 61));

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            "fk_email_verification_challenges_users_user_id");
    }

    [Fact]
    public async Task DeletingUser_CascadesEmailVerificationChallenge()
    {
        await using (EnmaDbContext setupContext = fixture.CreateDbContext())
        {
            AddUser(setupContext, FirstUserId, "cascade@enma.test");
            setupContext.EmailVerificationChallenges.Add(
                CreateChallenge(FirstUserId, 71));
            await setupContext.SaveChangesAsync();
        }

        await using (EnmaDbContext deleteContext = fixture.CreateDbContext())
        {
            User user = await deleteContext.Users
                .SingleAsync(candidate => candidate.Id == FirstUserId);
            deleteContext.Users.Remove(user);
            await deleteContext.SaveChangesAsync();
        }

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.False(await verificationContext.Users
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == FirstUserId));
        Assert.False(await verificationContext.EmailVerificationChallenges
            .AsNoTracking()
            .AnyAsync(candidate => candidate.UserId == FirstUserId));
    }

    [Fact]
    public async Task DatabaseSchema_HasExpectedExpirationIndex()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT index_metadata.indisunique,
                   count(*) AS column_count,
                   min(column_metadata.attname) AS column_name
            FROM pg_catalog.pg_index AS index_metadata
            JOIN pg_catalog.pg_class AS table_metadata
                ON table_metadata.oid = index_metadata.indrelid
            JOIN pg_catalog.pg_class AS index_relation
                ON index_relation.oid = index_metadata.indexrelid
            JOIN pg_catalog.pg_namespace AS schema_metadata
                ON schema_metadata.oid = table_metadata.relnamespace
            CROSS JOIN LATERAL unnest(index_metadata.indkey)
                WITH ORDINALITY AS indexed_columns(attnum, position)
            JOIN pg_catalog.pg_attribute AS column_metadata
                ON column_metadata.attrelid = table_metadata.oid
                AND column_metadata.attnum = indexed_columns.attnum
            WHERE schema_metadata.nspname = current_schema()
                AND table_metadata.relname = @table_name
                AND index_relation.relname = @index_name
            GROUP BY index_metadata.indisunique
            """,
            connection);
        command.Parameters.AddWithValue(
            "table_name",
            "email_verification_challenges");
        command.Parameters.AddWithValue(
            "index_name",
            "ix_email_verification_challenges_expires_at");

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.False(reader.GetBoolean(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal("expires_at", reader.GetString(2));
        Assert.False(await reader.ReadAsync());
    }

    private async Task AssertCheckConstraintAsync(
        Guid userId,
        byte[] tokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        string expectedConstraintName)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            async () => await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO email_verification_challenges
                    (user_id, email_at_issue, token_hash, created_at, expires_at)
                VALUES
                    ({userId}, {"constraint@enma.test"}, {tokenHash},
                     {createdAt}, {expiresAt})
                """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(expectedConstraintName, exception.ConstraintName);
    }

    private async Task SeedUserAsync(Guid userId, string email)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        AddUser(dbContext, userId, email);
        await dbContext.SaveChangesAsync();
    }

    private static void AddUser(
        EnmaDbContext dbContext,
        Guid userId,
        string email)
    {
        var user = new User("Email Verification User", email, CreatedAt);
        dbContext.Users.Add(user);
        dbContext.Entry(user).Property(candidate => candidate.Id).CurrentValue = userId;
    }

    private static EmailVerificationChallenge CreateChallenge(
        Guid userId,
        byte hashSeed)
    {
        return new EmailVerificationChallenge(
            userId,
            "challenge@enma.test",
            CreateTokenHash(hashSeed),
            CreatedAt,
            ExpiresAt);
    }

    private static EmailVerificationTokenHash CreateTokenHash(byte seed)
    {
        return new EmailVerificationTokenHash(CreateHashBytes(seed));
    }

    private static byte[] CreateHashBytes(byte seed)
    {
        return Enumerable.Range(0, 32)
            .Select(index => (byte)(seed + index))
            .ToArray();
    }

    private static void AssertPostgresException(
        DbUpdateException exception,
        string expectedSqlState,
        string expectedConstraintName)
    {
        PostgresException postgresException =
            Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(expectedSqlState, postgresException.SqlState);
        Assert.Equal(expectedConstraintName, postgresException.ConstraintName);
    }
}
