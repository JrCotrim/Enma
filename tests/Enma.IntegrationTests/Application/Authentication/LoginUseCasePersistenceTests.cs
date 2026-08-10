using Enma.Application.Authentication;
using Enma.Application.Security;
using Enma.Domain.Authentication;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.Infrastructure.Security;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Application.Authentication;

[Collection(PostgreSqlCollection.Name)]
public sealed class LoginUseCasePersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string Email = "login-owner@example.test";
    private const string Password = "Correct-Synthetic-Password-123!";
    private const string ChangedPassword = "Changed-Synthetic-Password-456!";

    private static readonly DateTimeOffset OperationTime = new(
        2026,
        8,
        10,
        14,
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
    public async Task ExecuteAsync_WithEligibleUser_PersistsExactlyOneHashedSessionAtCurrentCredentialVersion()
    {
        User user = await SeedUserAsync(emailVerified: true);
        await using EnmaDbContext lookupContext = fixture.CreateDbContext();
        CryptographicAuthenticationSessionHandleService handleService = new();
        LoginUseCase useCase = CreateUseCase(lookupContext, handleService);

        LoginResult result = await useCase.ExecuteAsync(Email, Password);

        Assert.Equal(LoginResultStatus.Succeeded, result.Status);
        string rawHandle = Assert.IsType<string>(result.SessionHandle);
        Assert.True(handleService.TryHashHandle(rawHandle, out var expectedHash));
        Assert.NotNull(expectedHash);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        AuthenticationSession persistedSession = await verificationContext
            .AuthenticationSessions
            .AsNoTracking()
            .SingleAsync();
        UserCredential persistedCredential = await verificationContext.UserCredentials
            .AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == user.Id);

        Assert.Equal(user.Id, persistedSession.UserId);
        Assert.Equal(expectedHash, persistedSession.SecretHash);
        Assert.NotEqual(
            rawHandle,
            Convert.ToBase64String(persistedSession.SecretHash.ToArray()));
        Assert.Equal(
            persistedCredential.CredentialVersion,
            persistedSession.CredentialVersionAtIssue);
        Assert.Equal(OperationTime, persistedSession.CreatedAt);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownValidEmail_DoesNotPersistSession()
    {
        await using EnmaDbContext lookupContext = fixture.CreateDbContext();
        LoginUseCase useCase = CreateUseCase(
            lookupContext,
            new CryptographicAuthenticationSessionHandleService());

        LoginResult result = await useCase.ExecuteAsync(
            "unknown-login-owner@example.test",
            Password);

        Assert.Equal(LoginResultStatus.InvalidCredentials, result.Status);
        Assert.Null(result.SessionHandle);
        await AssertNoSessionsAsync();
    }

    [Fact]
    public async Task ExecuteAsync_WithCredentialLessUser_DoesNotPersistSession()
    {
        await SeedUserWithoutCredentialAsync();
        await using EnmaDbContext lookupContext = fixture.CreateDbContext();
        LoginUseCase useCase = CreateUseCase(
            lookupContext,
            new CryptographicAuthenticationSessionHandleService());

        LoginResult result = await useCase.ExecuteAsync(Email, Password);

        Assert.Equal(LoginResultStatus.InvalidCredentials, result.Status);
        Assert.Null(result.SessionHandle);
        await AssertNoSessionsAsync();
    }

    [Fact]
    public async Task ExecuteAsync_WithUnverifiedUser_DoesNotPersistSession()
    {
        await SeedUserAsync(emailVerified: false);
        await using EnmaDbContext lookupContext = fixture.CreateDbContext();
        LoginUseCase useCase = CreateUseCase(
            lookupContext,
            new CryptographicAuthenticationSessionHandleService());

        LoginResult result = await useCase.ExecuteAsync(Email, Password);

        Assert.Equal(LoginResultStatus.InvalidCredentials, result.Status);
        Assert.Null(result.SessionHandle);
        await AssertNoSessionsAsync();
    }

    [Fact]
    public async Task ExecuteAsync_WithWrongPassword_DoesNotPersistSession()
    {
        await SeedUserAsync(emailVerified: true);
        await using EnmaDbContext lookupContext = fixture.CreateDbContext();
        LoginUseCase useCase = CreateUseCase(
            lookupContext,
            new CryptographicAuthenticationSessionHandleService());

        LoginResult result = await useCase.ExecuteAsync(
            Email,
            "wrong-synthetic-password");

        Assert.Equal(LoginResultStatus.InvalidCredentials, result.Status);
        Assert.Null(result.SessionHandle);
        await AssertNoSessionsAsync();
    }

    [Fact]
    public async Task ExecuteAsync_WhenCredentialChangesBeforeAuthoritativeIssuance_RejectsLoginWithoutSession()
    {
        User user = await SeedUserAsync(emailVerified: true);
        await using EnmaDbContext lookupContext = fixture.CreateDbContext();
        AspNetCorePasswordHasher passwordHasher = CreatePasswordHasher();
        var securePersistence = new AuthenticationSessionIssuancePersistence(
            CreateDbContextOptions());
        var changingPersistence = new CredentialChangingPersistence(
            fixture,
            securePersistence,
            passwordHasher);
        LoginUseCase useCase = CreateUseCase(
            lookupContext,
            new CryptographicAuthenticationSessionHandleService(),
            changingPersistence,
            passwordHasher);

        LoginResult result = await useCase.ExecuteAsync(Email, Password);

        Assert.Equal(LoginResultStatus.InvalidCredentials, result.Status);
        Assert.Null(result.SessionHandle);
        Assert.Equal(1, changingPersistence.CallCount);
        await AssertNoSessionsAsync();

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        UserCredential credential = await verificationContext.UserCredentials
            .AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == user.Id);
        Assert.Equal(2, credential.CredentialVersion);
        Assert.Equal(
            Enma.Application.Security.PasswordVerificationResult.Success,
            passwordHasher.VerifyHashedPassword(
                credential.PasswordHash,
                ChangedPassword));
    }

    private LoginUseCase CreateUseCase(
        EnmaDbContext lookupContext,
        CryptographicAuthenticationSessionHandleService handleService,
        IAuthenticationSessionIssuancePersistence? persistence = null,
        AspNetCorePasswordHasher? passwordHasher = null)
    {
        AspNetCorePasswordHasher effectivePasswordHasher =
            passwordHasher ?? CreatePasswordHasher();

        return new LoginUseCase(
            new AuthenticationIdentityLookup(lookupContext),
            effectivePasswordHasher,
            new LoginDummyPasswordHashProvider(effectivePasswordHasher),
            handleService,
            persistence ?? new AuthenticationSessionIssuancePersistence(
                CreateDbContextOptions()),
            new FixedTimeProvider(OperationTime));
    }

    private async Task<User> SeedUserAsync(bool emailVerified)
    {
        DateTimeOffset createdAt = OperationTime.AddDays(-1);
        var user = new User("Login Owner", Email, createdAt);

        if (emailVerified)
        {
            user.VerifyEmail(createdAt.AddMinutes(5));
        }

        var credential = new UserCredential(
            user.Id,
            CreatePasswordHasher().HashPassword(Password),
            createdAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Users.Add(user);
        dbContext.UserCredentials.Add(credential);
        await dbContext.SaveChangesAsync();

        return user;
    }

    private async Task SeedUserWithoutCredentialAsync()
    {
        DateTimeOffset createdAt = OperationTime.AddDays(-1);
        var user = new User("Credential-less Login Owner", Email, createdAt);
        user.VerifyEmail(createdAt.AddMinutes(5));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
    }

    private async Task AssertNoSessionsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.False(await dbContext.AuthenticationSessions
            .AsNoTracking()
            .AnyAsync());
    }

    private DbContextOptions<EnmaDbContext> CreateDbContextOptions()
    {
        return new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
    }

    private static AspNetCorePasswordHasher CreatePasswordHasher()
    {
        return new AspNetCorePasswordHasher(new PasswordHasher<object>());
    }

    private sealed class CredentialChangingPersistence(
        PostgreSqlFixture fixture,
        IAuthenticationSessionIssuancePersistence securePersistence,
        IPasswordHasher passwordHasher)
        : IAuthenticationSessionIssuancePersistence
    {
        public int CallCount { get; private set; }

        public async Task<AuthenticationSessionIssuancePersistenceResult> TryPersistAsync(
            AuthenticationSession session,
            string? upgradedPasswordHash,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            await using (EnmaDbContext dbContext = fixture.CreateDbContext())
            {
                UserCredential credential = await dbContext.UserCredentials
                    .SingleAsync(
                        candidate => candidate.UserId == session.UserId,
                        cancellationToken);
                credential.ChangePasswordHash(
                    passwordHasher.HashPassword(ChangedPassword),
                    OperationTime.AddMinutes(-1));
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return await securePersistence.TryPersistAsync(
                session,
                upgradedPasswordHash,
                cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}
