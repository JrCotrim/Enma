using Enma.Application.Authentication;
using Enma.Domain.Authentication;

namespace Enma.UnitTests.Application.Authentication;

public sealed class RequestEmailVerificationUseCaseTests
{
    private const string RawToken = "synthetic-email-verification-token";

    private static readonly Guid UserId = Guid.Parse(
        "11111111-2222-3333-4444-555555555555");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid-email")]
    public async Task ExecuteAsync_MalformedEmail_CompletesWithoutDependencies(
        string? email)
    {
        var dependencies = new TestDependencies();
        RequestEmailVerificationUseCase useCase = dependencies.CreateUseCase();

        await useCase.ExecuteAsync(email);

        Assert.Equal(0, dependencies.UserLookup.CallCount);
        Assert.Equal(0, dependencies.TokenService.GenerateCallCount);
        Assert.Equal(0, dependencies.Persistence.IssueCallCount);
        Assert.Equal(0, dependencies.Delivery.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownCanonicalEmail_CompletesAfterLookup()
    {
        var dependencies = new TestDependencies();
        RequestEmailVerificationUseCase useCase = dependencies.CreateUseCase();

        await useCase.ExecuteAsync("  UNKNOWN@EXAMPLE.TEST  ");

        Assert.Equal(1, dependencies.UserLookup.CallCount);
        Assert.Equal("unknown@example.test", dependencies.UserLookup.NormalizedEmail);
        Assert.Equal(0, dependencies.TokenService.GenerateCallCount);
        Assert.Equal(0, dependencies.Persistence.IssueCallCount);
        Assert.Equal(0, dependencies.Delivery.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_RejectedIssuance_CompletesWithoutDelivery()
    {
        var dependencies = new TestDependencies();
        dependencies.UserLookup.UserId = UserId;
        RequestEmailVerificationUseCase useCase = dependencies.CreateUseCase();
        using var cancellationSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            "user@example.test",
            cancellationSource.Token);

        Assert.Equal(1, dependencies.TokenService.GenerateCallCount);
        Assert.Equal(1, dependencies.Persistence.IssueCallCount);
        Assert.Equal(UserId, dependencies.Persistence.IssuedUserId);
        Assert.Same(
            dependencies.TokenService.TokenHash,
            dependencies.Persistence.IssuedTokenHash);
        Assert.Equal(
            TimeSpan.FromHours(1),
            dependencies.Persistence.TokenLifetime);
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            dependencies.Persistence.ResendCooldown);
        Assert.Equal(
            cancellationSource.Token,
            dependencies.Persistence.CancellationToken);
        Assert.Equal(0, dependencies.Delivery.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_EmailChangedAfterLookup_DeliversToIssuedEmail()
    {
        var dependencies = new TestDependencies();
        dependencies.UserLookup.UserId = UserId;
        dependencies.Persistence.IssuanceResult =
            EmailVerificationChallengeIssuancePersistenceResult.CreateSucceeded(
                "new@example.test");
        dependencies.Delivery.Result = EmailVerificationDeliveryResult.Delivered;
        RequestEmailVerificationUseCase useCase = dependencies.CreateUseCase();

        await useCase.ExecuteAsync("old@example.test");

        Assert.Equal(1, dependencies.Delivery.CallCount);
        Assert.Equal("new@example.test", dependencies.Delivery.Email);
        Assert.NotEqual("old@example.test", dependencies.Delivery.Email);
        Assert.Equal(RawToken, dependencies.Delivery.RawToken);
    }

    [Fact]
    public async Task ExecuteAsync_DeliveryFails_CompletesWithoutPersistenceRetry()
    {
        var dependencies = new TestDependencies();
        dependencies.UserLookup.UserId = UserId;
        dependencies.Persistence.IssuanceResult =
            EmailVerificationChallengeIssuancePersistenceResult.CreateSucceeded(
                "user@example.test");
        dependencies.Delivery.Result = EmailVerificationDeliveryResult.Failed;
        RequestEmailVerificationUseCase useCase = dependencies.CreateUseCase();

        await useCase.ExecuteAsync("user@example.test");

        Assert.Equal(1, dependencies.Persistence.IssueCallCount);
        Assert.Equal(0, dependencies.Persistence.ConsumeCallCount);
        Assert.Equal(1, dependencies.Delivery.CallCount);
    }

    private sealed class TestDependencies
    {
        public StubUserLookup UserLookup { get; } = new();

        public StubTokenService TokenService { get; } = new();

        public StubChallengePersistence Persistence { get; } = new();

        public StubDelivery Delivery { get; } = new();

        public RequestEmailVerificationUseCase CreateUseCase()
        {
            return new RequestEmailVerificationUseCase(
                UserLookup,
                TokenService,
                Persistence,
                Delivery);
        }
    }

    private sealed class StubUserLookup : IEmailVerificationUserLookup
    {
        public Guid? UserId { get; set; }

        public int CallCount { get; private set; }

        public string? NormalizedEmail { get; private set; }

        public Task<Guid?> FindUserIdByEmailAsync(
            string normalizedEmail,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            NormalizedEmail = normalizedEmail;
            return Task.FromResult(UserId);
        }
    }

    private sealed class StubTokenService : IEmailVerificationTokenService
    {
        public EmailVerificationTokenHash TokenHash { get; } =
            new(CreateHashBytes(1));

        public int GenerateCallCount { get; private set; }

        public string GenerateToken(out EmailVerificationTokenHash tokenHash)
        {
            GenerateCallCount++;
            tokenHash = TokenHash;
            return RawToken;
        }

        public bool TryHashToken(
            string? rawToken,
            out EmailVerificationTokenHash? tokenHash)
        {
            tokenHash = null;
            return false;
        }
    }

    private sealed class StubChallengePersistence
        : IEmailVerificationChallengePersistence
    {
        public EmailVerificationChallengeIssuancePersistenceResult IssuanceResult
        { get; set; } = EmailVerificationChallengeIssuancePersistenceResult.Rejected;

        public int IssueCallCount { get; private set; }

        public int ConsumeCallCount { get; private set; }

        public Guid IssuedUserId { get; private set; }

        public EmailVerificationTokenHash? IssuedTokenHash { get; private set; }

        public TimeSpan TokenLifetime { get; private set; }

        public TimeSpan ResendCooldown { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<EmailVerificationChallengeIssuancePersistenceResult>
            TryIssueOrRotateAsync(
                Guid userId,
                EmailVerificationTokenHash tokenHash,
                TimeSpan tokenLifetime,
                TimeSpan resendCooldown,
                CancellationToken cancellationToken = default)
        {
            IssueCallCount++;
            IssuedUserId = userId;
            IssuedTokenHash = tokenHash;
            TokenLifetime = tokenLifetime;
            ResendCooldown = resendCooldown;
            CancellationToken = cancellationToken;
            return Task.FromResult(IssuanceResult);
        }

        public Task<EmailVerificationChallengeConsumptionPersistenceResult>
            TryConsumeAsync(
                EmailVerificationTokenHash tokenHash,
                CancellationToken cancellationToken = default)
        {
            ConsumeCallCount++;
            return Task.FromResult(
                EmailVerificationChallengeConsumptionPersistenceResult.Rejected);
        }
    }

    private sealed class StubDelivery : IEmailVerificationDelivery
    {
        public EmailVerificationDeliveryResult Result { get; set; }

        public int CallCount { get; private set; }

        public string? Email { get; private set; }

        public string? RawToken { get; private set; }

        public Task<EmailVerificationDeliveryResult> DeliverAsync(
            string email,
            string rawToken,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Email = email;
            RawToken = rawToken;
            return Task.FromResult(Result);
        }
    }

    private static byte[] CreateHashBytes(byte seed)
    {
        return Enumerable.Range(0, 32)
            .Select(index => (byte)(seed + index))
            .ToArray();
    }
}
