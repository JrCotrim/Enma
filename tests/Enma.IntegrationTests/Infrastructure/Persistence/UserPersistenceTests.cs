using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class UserPersistenceTests(PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        1,
        2,
        3,
        4,
        5,
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
    public async Task SaveAndLoad_WithValidUser_PreservesAllFields()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = new("  Enma User  ", "  USER@EXAMPLE.COM  ", CreatedAt);

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        User persistedUser = await dbContext.Users
            .AsNoTracking()
            .SingleAsync();

        Assert.NotEqual(Guid.Empty, persistedUser.Id);
        Assert.Equal("Enma User", persistedUser.Name);
        Assert.Equal("user@example.com", persistedUser.Email);
        Assert.Null(persistedUser.EmailVerifiedAt);
        Assert.True(persistedUser.IsActive);
        Assert.Equal(CreatedAt, persistedUser.CreatedAt);
    }

    [Fact]
    public async Task VerifyEmail_ThenSave_PersistsEmailVerifiedAt()
    {
        User user = new("Enma User", "user@example.com", CreatedAt);
        user.VerifyEmail(user.CreatedAt);

        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync();
        }

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        User persistedUser = await verificationContext.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id);

        Assert.Equal(user.CreatedAt, persistedUser.EmailVerifiedAt);
    }

    [Fact]
    public async Task ChangeEmail_AfterVerification_ClearsPersistedEmailVerifiedAt()
    {
        User user = new("Enma User", "user@example.com", CreatedAt);
        user.VerifyEmail(CreatedAt);

        await using (EnmaDbContext createContext = fixture.CreateDbContext())
        {
            createContext.Users.Add(user);
            await createContext.SaveChangesAsync();
        }

        await using (EnmaDbContext updateContext = fixture.CreateDbContext())
        {
            User persistedUser = await updateContext.Users
                .SingleAsync(candidate => candidate.Id == user.Id);
            persistedUser.ChangeEmail("  UPDATED@EXAMPLE.COM  ");
            await updateContext.SaveChangesAsync();
        }

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        User updatedUser = await verificationContext.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id);

        Assert.Equal("updated@example.com", updatedUser.Email);
        Assert.Null(updatedUser.EmailVerifiedAt);
    }

    [Fact]
    public async Task DatabaseConstraint_RejectsEmailVerifiedAtBeforeCreatedAt()
    {
        User user = new("Enma User", "user@example.com", CreatedAt);

        await using (EnmaDbContext createContext = fixture.CreateDbContext())
        {
            createContext.Users.Add(user);
            await createContext.SaveChangesAsync();
        }

        await using (EnmaDbContext sqlContext = fixture.CreateDbContext())
        {
            PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() =>
                sqlContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE users
                    SET email_verified_at = created_at - INTERVAL '1 second'
                    WHERE id = {user.Id}
                    """));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("ck_users_email_verified_at", exception.ConstraintName);
        }

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        User persistedUser = await verificationContext.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id);

        Assert.Null(persistedUser.EmailVerifiedAt);
    }

    [Fact]
    public async Task SaveAndLoad_WithDeactivatedUser_PreservesInactiveState()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = new("Enma User", "user@example.com", CreatedAt);
        user.Deactivate();

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        User persistedUser = await dbContext.Users
            .AsNoTracking()
            .SingleAsync();

        Assert.False(persistedUser.IsActive);
    }

    [Fact]
    public async Task SaveChanges_WithDuplicateNormalizedEmail_ThrowsDbUpdateException()
    {
        await using (EnmaDbContext firstContext = fixture.CreateDbContext())
        {
            User firstUser = new(
                "First User",
                "Shared.Email@Example.com",
                CreatedAt);
            firstContext.Users.Add(firstUser);
            await firstContext.SaveChangesAsync();
        }

        await using EnmaDbContext secondContext = fixture.CreateDbContext();
        User secondUser = new(
            "Second User",
            "  SHARED.EMAIL@example.COM  ",
            CreatedAt.AddMinutes(1));
        secondContext.Users.Add(secondUser);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => secondContext.SaveChangesAsync());

        PostgresException postgresException =
            Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal("ux_users_email", postgresException.ConstraintName);
    }

    [Fact]
    public async Task SaveChanges_WithDifferentEmails_PersistsBothUsers()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User firstUser = new("First User", "first@example.com", CreatedAt);
        User secondUser = new(
            "Second User",
            "second@example.com",
            CreatedAt.AddMinutes(1));

        dbContext.Users.AddRange(firstUser, secondUser);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        string[] persistedEmails = await dbContext.Users
            .AsNoTracking()
            .OrderBy(user => user.Email)
            .Select(user => user.Email)
            .ToArrayAsync();

        Assert.Equal(["first@example.com", "second@example.com"], persistedEmails);
    }

    [Fact]
    public async Task SaveAndLoad_AfterNameAndEmailChanges_PersistsUpdatedValues()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = new("Original Name", "original@example.com", CreatedAt);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        user.Rename("  Updated Name  ");
        user.ChangeEmail("  UPDATED@EXAMPLE.COM  ");
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        User persistedUser = await dbContext.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id);

        Assert.Equal("Updated Name", persistedUser.Name);
        Assert.Equal("updated@example.com", persistedUser.Email);
    }
}
