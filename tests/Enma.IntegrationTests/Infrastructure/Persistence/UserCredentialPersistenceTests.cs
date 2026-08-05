using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class UserCredentialPersistenceTests(PostgreSqlFixture fixture)
    : IAsyncLifetime
{
    private const string InitialSyntheticHash = "synthetic-opaque-hash-v1";
    private const string UpdatedSyntheticHash = "synthetic-opaque-hash-v2";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        1,
        2,
        3,
        4,
        5,
        TimeSpan.Zero);

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
    public async Task SaveAndLoad_WithValidCredential_PreservesAllFields()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = CreateUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        UserCredential credential = new(
            user.Id,
            InitialSyntheticHash,
            CreatedAt);
        dbContext.UserCredentials.Add(credential);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        UserCredential persistedCredential = await dbContext.UserCredentials
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(user.Id, persistedCredential.UserId);
        Assert.Equal(InitialSyntheticHash, persistedCredential.PasswordHash);
        Assert.Equal(CreatedAt, persistedCredential.CreatedAt);
        Assert.Equal(CreatedAt, persistedCredential.PasswordChangedAt);
    }

    [Fact]
    public async Task SaveAndLoad_AfterPasswordHashChange_PersistsUpdatedValues()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = CreateUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        UserCredential credential = new(
            user.Id,
            InitialSyntheticHash,
            CreatedAt);
        dbContext.UserCredentials.Add(credential);
        await dbContext.SaveChangesAsync();

        credential.ChangePasswordHash(
            UpdatedSyntheticHash,
            PasswordChangedAt);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        UserCredential persistedCredential = await dbContext.UserCredentials
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(user.Id, persistedCredential.UserId);
        Assert.Equal(UpdatedSyntheticHash, persistedCredential.PasswordHash);
        Assert.Equal(CreatedAt, persistedCredential.CreatedAt);
        Assert.Equal(
            PasswordChangedAt,
            persistedCredential.PasswordChangedAt);
    }

    [Fact]
    public async Task SaveChanges_WithMissingUser_ThrowsDbUpdateException()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        UserCredential credential = new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            InitialSyntheticHash,
            CreatedAt);
        dbContext.UserCredentials.Add(credential);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            "fk_user_credentials_users_user_id");
    }

    [Fact]
    public async Task SaveChanges_WithSecondCredentialForSameUser_ThrowsDbUpdateException()
    {
        User user = CreateUser();

        await using (EnmaDbContext firstContext = fixture.CreateDbContext())
        {
            firstContext.Users.Add(user);
            await firstContext.SaveChangesAsync();

            UserCredential firstCredential = new(
                user.Id,
                InitialSyntheticHash,
                CreatedAt);
            firstContext.UserCredentials.Add(firstCredential);
            await firstContext.SaveChangesAsync();
        }

        await using EnmaDbContext secondContext = fixture.CreateDbContext();
        UserCredential secondCredential = new(
            user.Id,
            UpdatedSyntheticHash,
            CreatedAt);
        secondContext.UserCredentials.Add(secondCredential);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => secondContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.UniqueViolation,
            "pk_user_credentials");
    }

    [Fact]
    public async Task DeleteUser_WithCredential_CascadesCredentialDeletion()
    {
        User user = CreateUser();

        await using (EnmaDbContext setupContext = fixture.CreateDbContext())
        {
            setupContext.Users.Add(user);
            await setupContext.SaveChangesAsync();

            UserCredential credential = new(
                user.Id,
                InitialSyntheticHash,
                CreatedAt);
            setupContext.UserCredentials.Add(credential);
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
        Assert.False(await verificationContext.Users
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == user.Id));
        Assert.False(await verificationContext.UserCredentials
            .AsNoTracking()
            .AnyAsync(candidate => candidate.UserId == user.Id));
    }

    [Fact]
    public async Task Insert_WithPasswordChangedBeforeCreation_IsRejectedByDatabase()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = CreateUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        DateTimeOffset passwordChangedBeforeCreation = CreatedAt.AddMinutes(-1);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            async () => await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO user_credentials
                    (user_id, password_hash, created_at, password_changed_at)
                VALUES
                    ({user.Id}, {InitialSyntheticHash}, {CreatedAt}, {passwordChangedBeforeCreation})
                """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(
            "ck_user_credentials_password_changed_at",
            exception.ConstraintName);
    }

    private static User CreateUser()
    {
        return new User("Enma User", "user@example.com", CreatedAt);
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
