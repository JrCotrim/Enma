using System.Reflection;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;

namespace Enma.UnitTests.Application.Authentication;

public sealed class ValidateSessionUseCaseTests
{
    private const string RawHandle = "synthetic-session-handle";

    private static readonly Guid UserId = Guid.Parse(
        "6d609b75-6cd7-4ac0-9c6c-23b5a18452ca");

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        11,
        14,
        30,
        0,
        TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("malformed")]
    public async Task ExecuteAsync_WithMissingOrMalformedHandle_ReturnsUnauthenticatedWithoutPersistence(
        string? rawHandle)
    {
        TestDependencies dependencies = CreateDependencies(canHash: false);

        SessionValidationResult result = await dependencies.UseCase.ExecuteAsync(
            rawHandle);

        Assert.Equal(SessionValidationResultStatus.Unauthenticated, result.Status);
        Assert.Null(result.UserId);
        Assert.Equal(1, dependencies.HandleService.TryHashCallCount);
        Assert.Equal(rawHandle, dependencies.HandleService.RawHandle);
        Assert.Equal(0, dependencies.Persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithValidHandle_PassesOnlyHashAndOneNowToPersistence()
    {
        TestDependencies dependencies = CreateDependencies();
        using var cancellationTokenSource = new CancellationTokenSource();

        await dependencies.UseCase.ExecuteAsync(
            RawHandle,
            cancellationTokenSource.Token);

        Assert.Equal(1, dependencies.HandleService.TryHashCallCount);
        Assert.Equal(RawHandle, dependencies.HandleService.RawHandle);
        Assert.Equal(1, dependencies.Persistence.CallCount);
        Assert.Same(
            dependencies.HandleService.SecretHash,
            dependencies.Persistence.SecretHash);
        Assert.Equal(Now, dependencies.Persistence.Now);
        Assert.Equal(
            cancellationTokenSource.Token,
            dependencies.Persistence.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPersistenceRejects_ReturnsUnauthenticated()
    {
        TestDependencies dependencies = CreateDependencies();

        SessionValidationResult result = await dependencies.UseCase.ExecuteAsync(
            RawHandle);

        Assert.Equal(SessionValidationResultStatus.Unauthenticated, result.Status);
        Assert.Null(result.UserId);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPersistenceAccepts_ReturnsAuthenticatedUserIdOnly()
    {
        TestDependencies dependencies = CreateDependencies();
        dependencies.Persistence.UserId = UserId;

        SessionValidationResult result = await dependencies.UseCase.ExecuteAsync(
            RawHandle);

        Assert.Equal(SessionValidationResultStatus.Authenticated, result.Status);
        Assert.Equal(UserId, result.UserId);

        PropertyInfo[] publicInstanceProperties = typeof(SessionValidationResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.Equal(
            [nameof(SessionValidationResult.Status), nameof(SessionValidationResult.UserId)],
            publicInstanceProperties
                .Select(property => property.Name)
                .OrderBy(name => name)
                .ToArray());
    }

    [Fact]
    public void Authenticated_WithEmptyUserId_Throws()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => SessionValidationResult.Authenticated(Guid.Empty));

        Assert.Equal("userId", exception.ParamName);
    }

    private static TestDependencies CreateDependencies(bool canHash = true)
    {
        return new TestDependencies(canHash);
    }

    private sealed class TestDependencies
    {
        public TestDependencies(bool canHash)
        {
            HandleService = new FakeAuthenticationSessionHandleService(canHash);
            Persistence = new FakeAuthenticationSessionRuntimePersistence();
            UseCase = new ValidateSessionUseCase(
                HandleService,
                Persistence,
                new FixedTimeProvider(Now));
        }

        public FakeAuthenticationSessionHandleService HandleService { get; }

        public FakeAuthenticationSessionRuntimePersistence Persistence { get; }

        public ValidateSessionUseCase UseCase { get; }
    }

    private sealed class FakeAuthenticationSessionHandleService(bool canHash)
        : IAuthenticationSessionHandleService
    {
        public AuthenticationSessionSecretHash SecretHash { get; } = new(
            Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

        public int TryHashCallCount { get; private set; }

        public string? RawHandle { get; private set; }

        public string GenerateHandle(
            out AuthenticationSessionSecretHash secretHash)
        {
            throw new NotSupportedException();
        }

        public bool TryHashHandle(
            string? rawHandle,
            out AuthenticationSessionSecretHash? secretHash)
        {
            TryHashCallCount++;
            RawHandle = rawHandle;
            secretHash = canHash ? SecretHash : null;
            return canHash;
        }
    }

    private sealed class FakeAuthenticationSessionRuntimePersistence
        : IAuthenticationSessionRuntimePersistence
    {
        public Guid? UserId { get; set; }

        public int CallCount { get; private set; }

        public AuthenticationSessionSecretHash? SecretHash { get; private set; }

        public DateTimeOffset? Now { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<Guid?> TryValidateAndRenewAsync(
            AuthenticationSessionSecretHash secretHash,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            SecretHash = secretHash;
            Now = now;
            CancellationToken = cancellationToken;
            return Task.FromResult(UserId);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }
}
