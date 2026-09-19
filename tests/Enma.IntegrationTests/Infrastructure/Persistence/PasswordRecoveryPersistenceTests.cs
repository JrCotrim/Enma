using Enma.Application.Authentication;
using Enma.Application.Security;
using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class PasswordRecoveryPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string Email = "recovery-user@example.test";
    private const string OldPassword = "Old-Synthetic-Password-123!";
    private const string NewPassword = "New-Synthetic-Password-456!";
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Issue_EnforcesCooldownThenRotatesAndInvalidatesPreviousToken()
    {
        (User user, _) = await SeedVerifiedUserAsync();
        var tokenService = new CryptographicPasswordRecoveryTokenService();
        tokenService.GenerateToken(out var firstHash);
        tokenService.GenerateToken(out var secondHash);
        tokenService.GenerateToken(out var thirdHash);
        var time = new MutableTimeProvider(CreatedAt.AddMinutes(1));
        var persistence = new PasswordRecoveryPersistence(
            CreateOptions(),
            time,
            CreateHasher());

        PasswordRecoveryChallengeIssuanceResult issued =
            await persistence.TryIssueOrRotateAsync(
                user.Email,
                firstHash,
                TimeSpan.FromHours(1),
                TimeSpan.FromMinutes(1));
        PasswordRecoveryChallengeIssuanceResult throttled =
            await persistence.TryIssueOrRotateAsync(
                user.Email,
                secondHash,
                TimeSpan.FromHours(1),
                TimeSpan.FromMinutes(1));
        time.UtcNow = time.UtcNow.AddMinutes(1);
        PasswordRecoveryChallengeIssuanceResult rotated =
            await persistence.TryIssueOrRotateAsync(
                user.Email,
                thirdHash,
                TimeSpan.FromHours(1),
                TimeSpan.FromMinutes(1));

        Assert.True(issued.Succeeded);
        Assert.False(throttled.Succeeded);
        Assert.True(rotated.Succeeded);
        await using EnmaDbContext assertionContext = fixture.CreateDbContext();
        PasswordRecoveryChallenge challenge = await assertionContext
            .PasswordRecoveryChallenges.AsNoTracking().SingleAsync();
        Assert.Equal(thirdHash, challenge.TokenHash);
        Assert.NotEqual(firstHash, challenge.TokenHash);
        Assert.Equal(time.UtcNow.AddHours(1), challenge.ExpiresAt);
    }

    [Fact]
    public async Task Issue_RejectsUnavailableAccountsWithoutPersistingChallenge()
    {
        var unverified = new User(
            "Unverified Recovery User",
            "unverified-recovery-user@example.test",
            CreatedAt);
        var inactive = new User(
            "Inactive Recovery User",
            "inactive-recovery-user@example.test",
            CreatedAt);
        inactive.VerifyEmail(CreatedAt);
        inactive.Deactivate();
        var missingCredential = new User(
            "Passwordless Recovery User",
            "passwordless-recovery-user@example.test",
            CreatedAt);
        missingCredential.VerifyEmail(CreatedAt);
        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            dbContext.AddRange(
                unverified,
                inactive,
                missingCredential,
                new UserCredential(
                    unverified.Id,
                    CreateHasher().HashPassword(OldPassword),
                    CreatedAt),
                new UserCredential(
                    inactive.Id,
                    CreateHasher().HashPassword(OldPassword),
                    CreatedAt));
            await dbContext.SaveChangesAsync();
        }

        var tokenService = new CryptographicPasswordRecoveryTokenService();
        var persistence = new PasswordRecoveryPersistence(
            CreateOptions(),
            new FixedTimeProvider(CreatedAt.AddMinutes(1)),
            CreateHasher());

        foreach (string email in new[]
                 {
                     unverified.Email,
                     inactive.Email,
                     missingCredential.Email
                 })
        {
            tokenService.GenerateToken(out var tokenHash);
            PasswordRecoveryChallengeIssuanceResult result =
                await persistence.TryIssueOrRotateAsync(
                    email,
                    tokenHash,
                    TimeSpan.FromHours(1),
                    TimeSpan.FromMinutes(1));

            Assert.False(result.Succeeded);
        }

        await using EnmaDbContext assertionContext = fixture.CreateDbContext();
        Assert.Empty(await assertionContext.PasswordRecoveryChallenges.ToListAsync());
    }

    [Fact]
    public async Task Reset_ValidTokenAtomicallyChangesCredentialConsumesTokenAndInvalidatesSession()
    {
        (User user, UserCredential credential) = await SeedVerifiedUserAsync();
        var tokenService = new CryptographicPasswordRecoveryTokenService();
        string rawToken = tokenService.GenerateToken(out var tokenHash);
        var handleService = new CryptographicAuthenticationSessionHandleService();
        string rawHandle = handleService.GenerateHandle(out var sessionHash);
        var session = new AuthenticationSession(
            user.Id,
            sessionHash,
            credential.CredentialVersion,
            CreatedAt,
            CreatedAt.AddHours(1),
            CreatedAt.AddDays(1));

        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            dbContext.AuthenticationSessions.Add(session);
            dbContext.PasswordRecoveryChallenges.Add(new PasswordRecoveryChallenge(
                user.Id,
                user.Email,
                tokenHash,
                CreatedAt,
                CreatedAt.AddHours(1)));
            await dbContext.SaveChangesAsync();
        }

        var runtime = new AuthenticationSessionRuntimePersistence(CreateOptions());
        Assert.Equal(user.Id, await runtime.TryValidateAndRenewAsync(
            sessionHash,
            CreatedAt.AddMinutes(1)));
        Assert.True(tokenService.TryHashToken(rawToken, out var lookupHash));
        var hasher = CreateHasher();
        var persistence = new PasswordRecoveryPersistence(
            CreateOptions(),
            new FixedTimeProvider(CreatedAt.AddMinutes(2)),
            hasher);

        PasswordRecoveryResetPersistenceResult reused =
            await persistence.TryResetPasswordAsync(
                lookupHash!,
                OldPassword,
                hasher.HashPassword(OldPassword));

        Assert.Equal(
            PasswordRecoveryResetPersistenceResult.CurrentPasswordReuse,
            reused);
        await using (EnmaDbContext rejectionContext = fixture.CreateDbContext())
        {
            UserCredential unchanged = await rejectionContext.UserCredentials
                .AsNoTracking()
                .SingleAsync();
            Assert.Equal(credential.PasswordHash, unchanged.PasswordHash);
            Assert.Equal(credential.CredentialVersion, unchanged.CredentialVersion);
            Assert.Single(await rejectionContext.PasswordRecoveryChallenges
                .AsNoTracking()
                .ToListAsync());
        }
        Assert.Equal(user.Id, await runtime.TryValidateAndRenewAsync(
            sessionHash,
            CreatedAt.AddMinutes(3)));

        PasswordRecoveryResetPersistenceResult result =
            await persistence.TryResetPasswordAsync(
                lookupHash!,
                NewPassword,
                hasher.HashPassword(NewPassword));

        Assert.Equal(PasswordRecoveryResetPersistenceResult.Succeeded, result);
        await using EnmaDbContext assertionContext = fixture.CreateDbContext();
        UserCredential changed = await assertionContext.UserCredentials
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(credential.CredentialVersion + 1, changed.CredentialVersion);
        Assert.Equal(Enma.Application.Security.PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(changed.PasswordHash!, OldPassword));
        Assert.Equal(Enma.Application.Security.PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(changed.PasswordHash!, NewPassword));
        Assert.Empty(await assertionContext.PasswordRecoveryChallenges.ToListAsync());
        Assert.Null(await runtime.TryValidateAndRenewAsync(
            sessionHash,
            CreatedAt.AddMinutes(3)));
        Assert.Equal(
            PasswordRecoveryResetPersistenceResult.Rejected,
            await persistence.TryResetPasswordAsync(
                lookupHash!,
                "Another-Synthetic-Password-789!",
                hasher.HashPassword("Another-Synthetic-Password-789!")));
    }

    [Fact]
    public async Task Reset_GoogleOnlyCredential_EstablishesFirstLocalPassword()
    {
        var user = new User("Google User", "google@example.test", CreatedAt);
        user.VerifyEmail(CreatedAt);
        var credential = new UserCredential(user.Id, passwordHash: null, CreatedAt);
        var tokenService = new CryptographicPasswordRecoveryTokenService();
        tokenService.GenerateToken(out var tokenHash);
        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            dbContext.AddRange(
                user,
                credential,
                new PasswordRecoveryChallenge(
                    user.Id,
                    user.Email,
                    tokenHash,
                    CreatedAt,
                    CreatedAt.AddHours(1)));
            await dbContext.SaveChangesAsync();
        }

        var hasher = CreateHasher();
        PasswordRecoveryResetPersistenceResult result =
            await new PasswordRecoveryPersistence(
                    CreateOptions(),
                    new FixedTimeProvider(CreatedAt.AddMinutes(1)),
                    hasher)
                .TryResetPasswordAsync(
                    tokenHash,
                    NewPassword,
                    hasher.HashPassword(NewPassword));

        Assert.Equal(PasswordRecoveryResetPersistenceResult.Succeeded, result);
        await using EnmaDbContext assertionContext = fixture.CreateDbContext();
        UserCredential changed = await assertionContext.UserCredentials
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(2, changed.CredentialVersion);
        Assert.Equal(
            Enma.Application.Security.PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(changed.PasswordHash!, NewPassword));
    }

    [Fact]
    public async Task Reset_TokenForOneUserCannotChangeAnotherUsersCredential()
    {
        (User firstUser, _) = await SeedVerifiedUserAsync();
        (User secondUser, UserCredential secondCredential) = await SeedVerifiedUserAsync(
            "second-recovery-user@example.test",
            "Second Recovery User");
        var tokenService = new CryptographicPasswordRecoveryTokenService();
        tokenService.GenerateToken(out var firstUserTokenHash);
        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            dbContext.PasswordRecoveryChallenges.Add(new PasswordRecoveryChallenge(
                firstUser.Id,
                firstUser.Email,
                firstUserTokenHash,
                CreatedAt,
                CreatedAt.AddHours(1)));
            await dbContext.SaveChangesAsync();
        }

        var persistence = new PasswordRecoveryPersistence(
            CreateOptions(),
            new FixedTimeProvider(CreatedAt.AddMinutes(1)),
            CreateHasher());
        Assert.Equal(
            PasswordRecoveryResetPersistenceResult.Succeeded,
            await persistence.TryResetPasswordAsync(
                firstUserTokenHash,
                NewPassword,
                CreateHasher().HashPassword(NewPassword)));

        await using EnmaDbContext assertionContext = fixture.CreateDbContext();
        UserCredential unchangedSecond = await assertionContext.UserCredentials
            .AsNoTracking()
            .SingleAsync(item => item.UserId == secondUser.Id);
        Assert.Equal(secondCredential.PasswordHash, unchangedSecond.PasswordHash);
        Assert.Equal(1, unchangedSecond.CredentialVersion);
    }

    [Fact]
    public async Task Reset_ConcurrentReplayAllowsExactlyOneCredentialChange()
    {
        (User user, _) = await SeedVerifiedUserAsync();
        var tokenService = new CryptographicPasswordRecoveryTokenService();
        tokenService.GenerateToken(out var tokenHash);
        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            dbContext.PasswordRecoveryChallenges.Add(new PasswordRecoveryChallenge(
                user.Id,
                user.Email,
                tokenHash,
                CreatedAt,
                CreatedAt.AddHours(1)));
            await dbContext.SaveChangesAsync();
        }

        var hasher = CreateHasher();
        var time = new FixedTimeProvider(CreatedAt.AddMinutes(5));
        Task<PasswordRecoveryResetPersistenceResult>[] attempts =
        [
            new PasswordRecoveryPersistence(CreateOptions(), time, hasher)
                .TryResetPasswordAsync(
                    tokenHash,
                    NewPassword,
                    hasher.HashPassword(NewPassword)),
            new PasswordRecoveryPersistence(CreateOptions(), time, hasher)
                .TryResetPasswordAsync(
                    tokenHash,
                    "Other-Synthetic-Password-789!",
                    hasher.HashPassword("Other-Synthetic-Password-789!"))
        ];

        PasswordRecoveryResetPersistenceResult[] results = await Task.WhenAll(attempts);

        Assert.Single(results, result =>
            result == PasswordRecoveryResetPersistenceResult.Succeeded);
        Assert.Single(results, result =>
            result == PasswordRecoveryResetPersistenceResult.Rejected);
        await using EnmaDbContext assertionContext = fixture.CreateDbContext();
        Assert.Empty(await assertionContext.PasswordRecoveryChallenges.ToListAsync());
        Assert.Equal(2, (await assertionContext.UserCredentials.SingleAsync()).CredentialVersion);
    }

    [Fact]
    public async Task Reset_ExpiredTokenIsRejectedAndRemovedWithoutChangingCredential()
    {
        (User user, UserCredential credential) = await SeedVerifiedUserAsync();
        var tokenService = new CryptographicPasswordRecoveryTokenService();
        tokenService.GenerateToken(out var tokenHash);
        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            dbContext.PasswordRecoveryChallenges.Add(new PasswordRecoveryChallenge(
                user.Id,
                user.Email,
                tokenHash,
                CreatedAt,
                CreatedAt.AddMinutes(5)));
            await dbContext.SaveChangesAsync();
        }

        var persistence = new PasswordRecoveryPersistence(
            CreateOptions(),
            new FixedTimeProvider(CreatedAt.AddMinutes(5)),
            CreateHasher());
        PasswordRecoveryResetPersistenceResult result =
            await persistence.TryResetPasswordAsync(
                tokenHash,
                NewPassword,
                "replacement-hash");

        Assert.Equal(PasswordRecoveryResetPersistenceResult.Rejected, result);
        await using EnmaDbContext assertionContext = fixture.CreateDbContext();
        Assert.Equal(
            credential.PasswordHash,
            (await assertionContext.UserCredentials.AsNoTracking().SingleAsync()).PasswordHash);
        Assert.Empty(await assertionContext.PasswordRecoveryChallenges.ToListAsync());
    }

    private async Task<(User User, UserCredential Credential)> SeedVerifiedUserAsync(
        string email = Email,
        string name = "Recovery User")
    {
        var user = new User(name, email, CreatedAt);
        user.VerifyEmail(CreatedAt);
        var credential = new UserCredential(
            user.Id,
            CreateHasher().HashPassword(OldPassword),
            CreatedAt);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(user, credential);
        await dbContext.SaveChangesAsync();
        return (user, credential);
    }

    private DbContextOptions<EnmaDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

    private static AspNetCorePasswordHasher CreateHasher() =>
        new(new PasswordHasher<object>());

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
