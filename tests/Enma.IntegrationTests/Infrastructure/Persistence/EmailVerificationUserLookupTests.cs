using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class EmailVerificationUserLookupTests(PostgreSqlFixture fixture)
    : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        8,
        12,
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
    public async Task FindUserIdByEmailAsync_CanonicalExistingEmail_ReturnsExactUserId()
    {
        User user = await SeedUserAsync(
            "existing@example.test",
            includeCredential: true);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var lookup = new EmailVerificationUserLookup(dbContext);

        Guid? userId = await lookup.FindUserIdByEmailAsync(
            "existing@example.test");

        Assert.Equal(user.Id, userId);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task FindUserIdByEmailAsync_UnknownCanonicalEmail_ReturnsNull()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var lookup = new EmailVerificationUserLookup(dbContext);

        Guid? userId = await lookup.FindUserIdByEmailAsync(
            "unknown@example.test");

        Assert.Null(userId);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task FindUserIdByEmailAsync_UserWithoutCredential_ReturnsExactUserId()
    {
        User user = await SeedUserAsync(
            "without-credential@example.test",
            includeCredential: false);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var lookup = new EmailVerificationUserLookup(dbContext);

        Guid? userId = await lookup.FindUserIdByEmailAsync(user.Email);

        Assert.Equal(user.Id, userId);
        Assert.False(await dbContext.UserCredentials.AnyAsync());
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task FindUserIdByEmailAsync_AfterEmailChange_FindsOnlyNewEmail()
    {
        User user = await SeedUserAsync(
            "old@example.test",
            includeCredential: false);

        await using (EnmaDbContext updateContext = fixture.CreateDbContext())
        {
            User persistedUser = await updateContext.Users.SingleAsync(
                candidate => candidate.Id == user.Id);
            persistedUser.ChangeEmail("new@example.test");
            await updateContext.SaveChangesAsync();
        }

        await using EnmaDbContext lookupContext = fixture.CreateDbContext();
        var lookup = new EmailVerificationUserLookup(lookupContext);

        Guid? oldEmailUserId = await lookup.FindUserIdByEmailAsync(
            "old@example.test");
        Guid? newEmailUserId = await lookup.FindUserIdByEmailAsync(
            "new@example.test");

        Assert.Null(oldEmailUserId);
        Assert.Equal(user.Id, newEmailUserId);
    }

    private async Task<User> SeedUserAsync(
        string email,
        bool includeCredential)
    {
        var user = new User("Email Verification User", email, CreatedAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Users.Add(user);

        if (includeCredential)
        {
            dbContext.UserCredentials.Add(new UserCredential(
                user.Id,
                "synthetic-opaque-hash-email-verification-lookup",
                CreatedAt));
        }

        await dbContext.SaveChangesAsync();
        return user;
    }
}
