using System.Reflection;
using Enma.Application.Authentication;
using Enma.Application.Security;
using Enma.Domain.Authentication;
using Enma.Domain.Users;

namespace Enma.UnitTests.Application.Authentication;

public sealed class LoginUseCaseTests
{
    private const string Email = "owner@example.test";
    private const string Password = "synthetic-password";
    private const string StoredPasswordHash = "synthetic-stored-password-hash";
    private const string DummyPasswordHash = "synthetic-dummy-password-hash";
    private const string UpgradedPasswordHash = "synthetic-upgraded-password-hash";
    private const string RawSessionHandle = "synthetic-raw-session-handle";

    private static readonly Guid UserId = Guid.Parse(
        "a80837ff-090e-4b97-adc0-cc38618b1d01");

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        10,
        12,
        30,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsync_WithEligibleIdentityAndCorrectPassword_IssuesSession()
    {
        TestDependencies dependencies = CreateDependencies(CreateIdentity());

        LoginResult result = await dependencies.UseCase.ExecuteAsync(
            "  OWNER@EXAMPLE.TEST ",
            Password);

        Assert.Equal(LoginResultStatus.Succeeded, result.Status);
        Assert.Equal(RawSessionHandle, result.SessionHandle);
        Assert.Equal(1, dependencies.IdentityLookup.CallCount);
        Assert.Equal(Email, dependencies.IdentityLookup.NormalizedEmail);
        Assert.Equal(
            AuthenticationIdentityLoadMode.ReadOnly,
            dependencies.IdentityLookup.LoadMode);
        Assert.Equal(1, dependencies.PasswordHasher.VerifyCallCount);
        Assert.Equal(StoredPasswordHash, dependencies.PasswordHasher.PasswordHash);
        Assert.Equal(1, dependencies.SessionHandleService.GenerateCallCount);
        Assert.Equal(1, dependencies.SessionPersistence.CallCount);
        Assert.Null(dependencies.SessionPersistence.UpgradedPasswordHash);

        AuthenticationSession session = Assert.IsType<AuthenticationSession>(
            dependencies.SessionPersistence.Session);
        Assert.Equal(UserId, session.UserId);
        Assert.Equal(1, session.CredentialVersionAtIssue);
        Assert.Equal(CreatedAt, session.CreatedAt);
        Assert.Equal(
            CreatedAt.Add(AuthenticationSessionPolicy.IdleLifetime),
            session.IdleExpiresAt);
        Assert.Equal(
            CreatedAt.Add(AuthenticationSessionPolicy.AbsoluteLifetime),
            session.AbsoluteExpiresAt);
        Assert.Null(session.SelectedOrganizationId);
        Assert.Equal(
            dependencies.SessionHandleService.SecretHash,
            session.SecretHash);
    }

    [Fact]
    public async Task ExecuteAsync_WithMalformedEmail_ReturnsInvalidCredentialsWithoutIssuance()
    {
        TestDependencies dependencies = CreateDependencies(CreateIdentity());

        LoginResult result = await dependencies.UseCase.ExecuteAsync(
            "not-an-email",
            Password);

        AssertInvalidWithoutIssuance(result, dependencies);
        Assert.Equal(0, dependencies.IdentityLookup.CallCount);
        Assert.Equal(0, dependencies.PasswordHasher.VerifyCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithNullPassword_ReturnsInvalidCredentialsWithoutVerification()
    {
        TestDependencies dependencies = CreateDependencies(CreateIdentity());

        LoginResult result = await dependencies.UseCase.ExecuteAsync(Email, null);

        AssertInvalidWithoutIssuance(result, dependencies);
        Assert.Equal(0, dependencies.IdentityLookup.CallCount);
        Assert.Equal(0, dependencies.PasswordHasher.VerifyCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownUserAndSuccessfulDummyVerification_ReturnsInvalidCredentialsWithoutIssuance()
    {
        TestDependencies dependencies = CreateDependencies(null);
        dependencies.PasswordHasher.VerificationResult =
            PasswordVerificationResult.Success;

        LoginResult result = await dependencies.UseCase.ExecuteAsync(
            Email,
            Password);

        AssertInvalidWithoutIssuance(result, dependencies);
        Assert.Equal(1, dependencies.IdentityLookup.CallCount);
        Assert.Equal(1, dependencies.PasswordHasher.VerifyCallCount);
        Assert.Equal(DummyPasswordHash, dependencies.PasswordHasher.PasswordHash);
        Assert.Equal(Password, dependencies.PasswordHasher.ProvidedPassword);
    }

    [Fact]
    public async Task ExecuteAsync_WithInactiveUser_ReturnsInvalidCredentialsWithoutIssuance()
    {
        TestDependencies dependencies = CreateDependencies(
            CreateIdentity(isActive: false));

        LoginResult result = await dependencies.UseCase.ExecuteAsync(
            Email,
            Password);

        AssertInvalidWithoutIssuance(result, dependencies);
        Assert.Equal(1, dependencies.PasswordHasher.VerifyCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnverifiedUser_ReturnsInvalidCredentialsWithoutIssuance()
    {
        TestDependencies dependencies = CreateDependencies(
            CreateIdentity(emailVerified: false));

        LoginResult result = await dependencies.UseCase.ExecuteAsync(
            Email,
            Password);

        AssertInvalidWithoutIssuance(result, dependencies);
        Assert.Equal(1, dependencies.PasswordHasher.VerifyCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithMissingCredential_ReturnsInvalidCredentialsWithoutIssuance()
    {
        TestDependencies dependencies = CreateDependencies(
            CreateIdentity(includeCredential: false));

        LoginResult result = await dependencies.UseCase.ExecuteAsync(
            Email,
            Password);

        AssertInvalidWithoutIssuance(result, dependencies);
        Assert.Equal(1, dependencies.PasswordHasher.VerifyCallCount);
        Assert.Equal(DummyPasswordHash, dependencies.PasswordHasher.PasswordHash);
        Assert.Equal(Password, dependencies.PasswordHasher.ProvidedPassword);
    }

    [Fact]
    public async Task ExecuteAsync_WithWrongPassword_ReturnsInvalidCredentialsWithoutIssuance()
    {
        TestDependencies dependencies = CreateDependencies(CreateIdentity());
        dependencies.PasswordHasher.VerificationResult =
            PasswordVerificationResult.Failed;

        LoginResult result = await dependencies.UseCase.ExecuteAsync(
            Email,
            "wrong-password");

        AssertInvalidWithoutIssuance(result, dependencies);
        Assert.Equal(1, dependencies.PasswordHasher.VerifyCallCount);
        Assert.Equal(StoredPasswordHash, dependencies.PasswordHasher.PasswordHash);
        Assert.NotEqual(DummyPasswordHash, dependencies.PasswordHasher.PasswordHash);
        Assert.Equal("wrong-password", dependencies.PasswordHasher.ProvidedPassword);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnsupportedVerificationResult_FailsClosedWithoutIssuance()
    {
        TestDependencies dependencies = CreateDependencies(CreateIdentity());
        dependencies.PasswordHasher.VerificationResult =
            (PasswordVerificationResult)999;

        LoginResult result = await dependencies.UseCase.ExecuteAsync(
            Email,
            Password);

        AssertInvalidWithoutIssuance(result, dependencies);
        Assert.Equal(1, dependencies.PasswordHasher.VerifyCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAuthoritativeIssuanceRejects_ReturnsInvalidCredentialsWithoutHandle()
    {
        TestDependencies dependencies = CreateDependencies(CreateIdentity());
        dependencies.SessionPersistence.Result =
            AuthenticationSessionIssuancePersistenceResult.Rejected;

        LoginResult result = await dependencies.UseCase.ExecuteAsync(
            Email,
            Password);

        Assert.Equal(LoginResultStatus.InvalidCredentials, result.Status);
        Assert.Null(result.SessionHandle);
        Assert.Equal(1, dependencies.SessionHandleService.GenerateCallCount);
        Assert.Equal(1, dependencies.SessionPersistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPasswordNeedsRehash_PersistsUpgradeWithSession()
    {
        TestDependencies dependencies = CreateDependencies(CreateIdentity());
        dependencies.PasswordHasher.VerificationResult =
            PasswordVerificationResult.SuccessRehashNeeded;

        LoginResult result = await dependencies.UseCase.ExecuteAsync(
            Email,
            Password);

        Assert.Equal(LoginResultStatus.Succeeded, result.Status);
        Assert.Equal(1, dependencies.PasswordHasher.HashCallCount);
        Assert.Equal(Password, dependencies.PasswordHasher.PasswordToHash);
        Assert.Equal(
            UpgradedPasswordHash,
            dependencies.SessionPersistence.UpgradedPasswordHash);
    }

    [Fact]
    public async Task ExecuteAsync_WithSuccessfulIssuance_ReturnsOnlyStatusAndRawHandle()
    {
        TestDependencies dependencies = CreateDependencies(CreateIdentity());

        LoginResult result = await dependencies.UseCase.ExecuteAsync(
            Email,
            Password);

        PropertyInfo[] publicInstanceProperties = typeof(LoginResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.Equal(LoginResultStatus.Succeeded, result.Status);
        Assert.Equal(RawSessionHandle, result.SessionHandle);
        Assert.Equal(
            [nameof(LoginResult.SessionHandle), nameof(LoginResult.Status)],
            publicInstanceProperties
                .Select(property => property.Name)
                .OrderBy(name => name)
                .ToArray());
    }

    private static void AssertInvalidWithoutIssuance(
        LoginResult result,
        TestDependencies dependencies)
    {
        Assert.Equal(LoginResultStatus.InvalidCredentials, result.Status);
        Assert.Null(result.SessionHandle);
        Assert.Equal(0, dependencies.SessionHandleService.GenerateCallCount);
        Assert.Equal(0, dependencies.SessionPersistence.CallCount);
    }

    private static TestDependencies CreateDependencies(
        AuthenticationIdentity? identity)
    {
        return new TestDependencies(identity);
    }

    private static AuthenticationIdentity CreateIdentity(
        bool isActive = true,
        bool emailVerified = true,
        bool includeCredential = true)
    {
        UserCredential? credential = includeCredential
            ? new UserCredential(UserId, StoredPasswordHash, CreatedAt.AddHours(-1))
            : null;

        return new AuthenticationIdentity(
            UserId,
            Email,
            isActive,
            emailVerified ? CreatedAt.AddMinutes(-30) : null,
            credential);
    }

    private sealed class TestDependencies
    {
        public TestDependencies(AuthenticationIdentity? identity)
        {
            IdentityLookup = new FakeAuthenticationIdentityLookup(identity);
            PasswordHasher = new FakePasswordHasher();
            DummyPasswordHashProvider = new FakeLoginDummyPasswordHashProvider();
            SessionHandleService = new FakeAuthenticationSessionHandleService();
            SessionPersistence = new FakeAuthenticationSessionIssuancePersistence();
            UseCase = new LoginUseCase(
                IdentityLookup,
                PasswordHasher,
                DummyPasswordHashProvider,
                SessionHandleService,
                SessionPersistence,
                new FixedTimeProvider(CreatedAt));
        }

        public FakeAuthenticationIdentityLookup IdentityLookup { get; }

        public FakePasswordHasher PasswordHasher { get; }

        public FakeLoginDummyPasswordHashProvider DummyPasswordHashProvider { get; }

        public FakeAuthenticationSessionHandleService SessionHandleService { get; }

        public FakeAuthenticationSessionIssuancePersistence SessionPersistence { get; }

        public LoginUseCase UseCase { get; }
    }

    private sealed class FakeAuthenticationIdentityLookup(
        AuthenticationIdentity? identity) : IAuthenticationIdentityLookup
    {
        public int CallCount { get; private set; }

        public string? NormalizedEmail { get; private set; }

        public AuthenticationIdentityLoadMode? LoadMode { get; private set; }

        public Task<AuthenticationIdentity?> FindByNormalizedEmailAsync(
            string normalizedEmail,
            AuthenticationIdentityLoadMode loadMode,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            NormalizedEmail = normalizedEmail;
            LoadMode = loadMode;
            return Task.FromResult(identity);
        }
    }

    private sealed class FakeLoginDummyPasswordHashProvider
        : ILoginDummyPasswordHashProvider
    {
        public string PasswordHash => DummyPasswordHash;
    }

    private sealed class FakePasswordHasher : IPasswordHasher
    {
        public PasswordVerificationResult VerificationResult { get; set; } =
            PasswordVerificationResult.Success;

        public int VerifyCallCount { get; private set; }

        public int HashCallCount { get; private set; }

        public string? PasswordHash { get; private set; }

        public string? ProvidedPassword { get; private set; }

        public string? PasswordToHash { get; private set; }

        public string HashPassword(string password)
        {
            HashCallCount++;
            PasswordToHash = password;
            return UpgradedPasswordHash;
        }

        public PasswordVerificationResult VerifyHashedPassword(
            string passwordHash,
            string providedPassword)
        {
            VerifyCallCount++;
            PasswordHash = passwordHash;
            ProvidedPassword = providedPassword;
            return VerificationResult;
        }
    }

    private sealed class FakeAuthenticationSessionHandleService
        : IAuthenticationSessionHandleService
    {
        public AuthenticationSessionSecretHash SecretHash { get; } = new(
            Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

        public int GenerateCallCount { get; private set; }

        public string GenerateHandle(
            out AuthenticationSessionSecretHash secretHash)
        {
            GenerateCallCount++;
            secretHash = SecretHash;
            return RawSessionHandle;
        }

        public bool TryHashHandle(
            string? rawHandle,
            out AuthenticationSessionSecretHash? secretHash)
        {
            secretHash = null;
            return false;
        }
    }

    private sealed class FakeAuthenticationSessionIssuancePersistence
        : IAuthenticationSessionIssuancePersistence
    {
        public AuthenticationSessionIssuancePersistenceResult Result { get; set; } =
            AuthenticationSessionIssuancePersistenceResult.Succeeded;

        public int CallCount { get; private set; }

        public AuthenticationSession? Session { get; private set; }

        public string? UpgradedPasswordHash { get; private set; }

        public Task<AuthenticationSessionIssuancePersistenceResult> TryPersistAsync(
            AuthenticationSession session,
            string? upgradedPasswordHash,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Session = session;
            UpgradedPasswordHash = upgradedPasswordHash;
            return Task.FromResult(Result);
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
