using System.Reflection;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;

namespace Enma.UnitTests.Application.Authentication;

public sealed class RevokeSessionUseCaseTests
{
    private const string RawHandle = "synthetic-session-handle";

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        11,
        16,
        0,
        0,
        TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("malformed")]
    public async Task ExecuteAsync_WithMissingOrMalformedHandle_CompletesWithoutPersistence(
        string? rawHandle)
    {
        TestDependencies dependencies = CreateDependencies(canHash: false);

        await dependencies.UseCase.ExecuteAsync(rawHandle);

        Assert.Equal(1, dependencies.HandleService.TryHashCallCount);
        Assert.Equal(rawHandle, dependencies.HandleService.RawHandle);
        Assert.Equal(0, dependencies.Persistence.CallCount);
        Assert.Equal(0, dependencies.TimeProvider.GetUtcNowCallCount);
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
        Assert.Equal(1, dependencies.TimeProvider.GetUtcNowCallCount);
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
    public async Task ExecuteAsync_WhenPersistenceCompletesForAnySessionState_ExposesNoDistinguishableResult()
    {
        TestDependencies unknownDependencies = CreateDependencies();
        TestDependencies revokedDependencies = CreateDependencies();

        await unknownDependencies.UseCase.ExecuteAsync(RawHandle);
        await revokedDependencies.UseCase.ExecuteAsync(RawHandle);

        MethodInfo? executeMethod = typeof(RevokeSessionUseCase).GetMethod(
            nameof(RevokeSessionUseCase.ExecuteAsync),
            [typeof(string), typeof(CancellationToken)]);

        Assert.NotNull(executeMethod);
        Assert.Equal(typeof(Task), executeMethod.ReturnType);
        Assert.Equal(1, unknownDependencies.Persistence.CallCount);
        Assert.Equal(1, revokedDependencies.Persistence.CallCount);
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
            Persistence = new FakeAuthenticationSessionRevocationPersistence();
            TimeProvider = new CountingTimeProvider(Now);
            UseCase = new RevokeSessionUseCase(
                HandleService,
                Persistence,
                TimeProvider);
        }

        public FakeAuthenticationSessionHandleService HandleService { get; }

        public FakeAuthenticationSessionRevocationPersistence Persistence { get; }

        public CountingTimeProvider TimeProvider { get; }

        public RevokeSessionUseCase UseCase { get; }
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

    private sealed class FakeAuthenticationSessionRevocationPersistence
        : IAuthenticationSessionRevocationPersistence
    {
        public int CallCount { get; private set; }

        public AuthenticationSessionSecretHash? SecretHash { get; private set; }

        public DateTimeOffset? Now { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task RevokeAsync(
            AuthenticationSessionSecretHash secretHash,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            SecretHash = secretHash;
            Now = now;
            CancellationToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public int GetUtcNowCallCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            GetUtcNowCallCount++;
            return now;
        }
    }
}
