using Enma.Application.Authentication;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Queries;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuthenticationIdentityLookupTests(PostgreSqlFixture fixture)
    : IAsyncLifetime
{
    private const string NormalizedEmail = "authentication.identity@example.test";
    private const string InitialSyntheticPasswordHash =
        "synthetic-opaque-hash-authentication-lookup-001";
    private const string ReplacementSyntheticPasswordHash =
        "synthetic-opaque-hash-authentication-lookup-002";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        6,
        11,
        12,
        13,
        TimeSpan.Zero);

    private static readonly DateTimeOffset EmailVerifiedAt = CreatedAt.AddMinutes(10);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FindByNormalizedEmailAsync_ReadOnlyExistingUserWithCredential_ReturnsIdentityWithoutTracking()
    {
        User user = await SeedUserAsync(includeCredential: true);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var lookup = new AuthenticationIdentityLookup(dbContext);

        AuthenticationIdentity? identity = await lookup.FindByNormalizedEmailAsync(
            NormalizedEmail,
            AuthenticationIdentityLoadMode.ReadOnly);

        Assert.NotNull(identity);
        Assert.Equal(user.Id, identity.UserId);
        Assert.Equal(user.Email, identity.Email);
        Assert.Equal(user.IsActive, identity.IsActive);
        Assert.Equal(user.EmailVerifiedAt, identity.EmailVerifiedAt);
        Assert.NotNull(identity.Credential);
        Assert.Equal(user.Id, identity.Credential.UserId);
        Assert.Equal(CreatedAt, identity.Credential.PasswordChangedAt);
        Assert.Equal(1, identity.Credential.CredentialVersion);
        Assert.Equal(0, string.CompareOrdinal(
            InitialSyntheticPasswordHash,
            identity.Credential.PasswordHash));
        Assert.Empty(dbContext.ChangeTracker.Entries<User>());
        Assert.Empty(dbContext.ChangeTracker.Entries<UserCredential>());
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task FindByNormalizedEmailAsync_ReadOnlyExistingUserWithoutCredential_ReturnsIdentityWithNullCredential()
    {
        User user = await SeedUserAsync(includeCredential: false);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var lookup = new AuthenticationIdentityLookup(dbContext);

        AuthenticationIdentity? identity = await lookup.FindByNormalizedEmailAsync(
            NormalizedEmail,
            AuthenticationIdentityLoadMode.ReadOnly);

        Assert.NotNull(identity);
        Assert.Equal(user.Id, identity.UserId);
        Assert.Equal(user.Email, identity.Email);
        Assert.Equal(user.IsActive, identity.IsActive);
        Assert.Equal(user.EmailVerifiedAt, identity.EmailVerifiedAt);
        Assert.Null(identity.Credential);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task FindByNormalizedEmailAsync_UnknownEmail_ReturnsNull()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var lookup = new AuthenticationIdentityLookup(dbContext);

        AuthenticationIdentity? identity = await lookup.FindByNormalizedEmailAsync(
            "unknown@example.test",
            AuthenticationIdentityLoadMode.ReadOnly);

        Assert.Null(identity);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task FindByNormalizedEmailAsync_NonNormalizedEmail_ThrowsBeforeDatabaseLookup()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var lookup = new AuthenticationIdentityLookup(dbContext);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            lookup.FindByNormalizedEmailAsync(
                "  AUTHENTICATION.IDENTITY@EXAMPLE.TEST  ",
                AuthenticationIdentityLoadMode.ReadOnly));

        Assert.Equal("normalizedEmail", exception.ParamName);
        Assert.Contains(
            AuthenticationIdentityErrors.EmailMustBeNormalized,
            exception.Message);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task FindByNormalizedEmailAsync_ForCredentialUpgrade_TracksCredentialButNotUser()
    {
        User user = await SeedUserAsync(includeCredential: true);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var lookup = new AuthenticationIdentityLookup(dbContext);

        AuthenticationIdentity? identity = await lookup.FindByNormalizedEmailAsync(
            NormalizedEmail,
            AuthenticationIdentityLoadMode.ForCredentialUpgrade);

        Assert.NotNull(identity);
        Assert.NotNull(identity.Credential);
        var trackedCredential = Assert.Single(
            dbContext.ChangeTracker.Entries<UserCredential>());
        Assert.Empty(dbContext.ChangeTracker.Entries<User>());
        Assert.Single(dbContext.ChangeTracker.Entries());
        Assert.Same(identity.Credential, trackedCredential.Entity);
        Assert.Equal(user.Id, trackedCredential.Entity.UserId);
        Assert.Equal(EntityState.Unchanged, trackedCredential.State);
    }

    [Fact]
    public async Task FindByNormalizedEmailAsync_ForCredentialUpgrade_AllowsTransparentRehashPersistence()
    {
        User user = await SeedUserAsync(includeCredential: true);
        DateTimeOffset originalPasswordChangedAt;
        long originalCredentialVersion;

        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            var lookup = new AuthenticationIdentityLookup(dbContext);
            AuthenticationIdentity? identity = await lookup.FindByNormalizedEmailAsync(
                NormalizedEmail,
                AuthenticationIdentityLoadMode.ForCredentialUpgrade);
            UserCredential credential = Assert.IsType<UserCredential>(identity?.Credential);
            originalPasswordChangedAt = credential.PasswordChangedAt;
            originalCredentialVersion = credential.CredentialVersion;

            credential.UpgradePasswordHash(ReplacementSyntheticPasswordHash);
            await dbContext.SaveChangesAsync();
        }

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        UserCredential persistedCredential = await verificationContext.UserCredentials
            .AsNoTracking()
            .SingleAsync(credential => credential.UserId == user.Id);

        Assert.Equal(0, string.CompareOrdinal(
            ReplacementSyntheticPasswordHash,
            persistedCredential.PasswordHash));
        Assert.Equal(
            originalPasswordChangedAt,
            persistedCredential.PasswordChangedAt);
        Assert.Equal(
            originalCredentialVersion,
            persistedCredential.CredentialVersion);
    }

    [Fact]
    public async Task FindByNormalizedEmailAsync_WithUndefinedLoadMode_ThrowsBeforeDatabaseLookup()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var lookup = new AuthenticationIdentityLookup(dbContext);
        var undefinedLoadMode = (AuthenticationIdentityLoadMode)999;

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            lookup.FindByNormalizedEmailAsync(
                NormalizedEmail,
                undefinedLoadMode));

        Assert.Equal("loadMode", exception.ParamName);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    private async Task<User> SeedUserAsync(bool includeCredential)
    {
        var user = new User(
            "Authentication Identity User",
            NormalizedEmail,
            CreatedAt);
        user.VerifyEmail(EmailVerifiedAt);
        user.Deactivate();

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Users.Add(user);

        if (includeCredential)
        {
            dbContext.UserCredentials.Add(new UserCredential(
                user.Id,
                InitialSyntheticPasswordHash,
                CreatedAt));
        }

        await dbContext.SaveChangesAsync();
        return user;
    }
}
