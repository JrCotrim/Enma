using Enma.Application.Authentication;
using Enma.Domain.Authentication;

namespace Enma.UnitTests.Application.Authentication;

public sealed class VerifyEmailUseCaseTests
{
    private const string RawToken = "synthetic-email-verification-token";

    [Fact]
    public async Task ExecuteAsync_MalformedToken_ReturnsInvalidWithoutPersistence()
    {
        var tokenService = new StubTokenService();
        var persistence = new StubChallengePersistence();
        var useCase = new VerifyEmailUseCase(tokenService, persistence);

        VerifyEmailResult result = await useCase.ExecuteAsync(null);

        Assert.Equal(VerifyEmailResult.Invalid, result);
        Assert.Equal(1, tokenService.HashCallCount);
        Assert.Null(tokenService.HashedRawToken);
        Assert.Equal(0, tokenService.GenerateCallCount);
        Assert.Equal(0, persistence.IssueCallCount);
        Assert.Equal(0, persistence.ConsumeCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ValidTokenRejectedByPersistence_ReturnsInvalid()
    {
        var tokenService = new StubTokenService { HashSucceeds = true };
        var persistence = new StubChallengePersistence();
        var useCase = new VerifyEmailUseCase(tokenService, persistence);
        using var cancellationSource = new CancellationTokenSource();

        VerifyEmailResult result = await useCase.ExecuteAsync(
            RawToken,
            cancellationSource.Token);

        Assert.Equal(VerifyEmailResult.Invalid, result);
        Assert.Equal(1, persistence.ConsumeCallCount);
        Assert.Same(tokenService.TokenHash, persistence.ConsumedTokenHash);
        Assert.Equal(cancellationSource.Token, persistence.CancellationToken);
        Assert.Equal(0, tokenService.GenerateCallCount);
        Assert.Equal(0, persistence.IssueCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ValidTokenConsumedByPersistence_ReturnsSucceeded()
    {
        var tokenService = new StubTokenService { HashSucceeds = true };
        var persistence = new StubChallengePersistence
        {
            ConsumptionResult =
                EmailVerificationChallengeConsumptionPersistenceResult.Succeeded
        };
        var useCase = new VerifyEmailUseCase(tokenService, persistence);

        VerifyEmailResult result = await useCase.ExecuteAsync(RawToken);

        Assert.Equal(VerifyEmailResult.Succeeded, result);
        Assert.Equal(1, persistence.ConsumeCallCount);
        Assert.Same(tokenService.TokenHash, persistence.ConsumedTokenHash);
        Assert.Equal(0, tokenService.GenerateCallCount);
        Assert.Equal(0, persistence.IssueCallCount);
    }

    private sealed class StubTokenService : IEmailVerificationTokenService
    {
        public EmailVerificationTokenHash TokenHash { get; } =
            new(CreateHashBytes(11));

        public bool HashSucceeds { get; set; }

        public int GenerateCallCount { get; private set; }

        public int HashCallCount { get; private set; }

        public string? HashedRawToken { get; private set; }

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
            HashCallCount++;
            HashedRawToken = rawToken;
            tokenHash = HashSucceeds ? TokenHash : null;
            return HashSucceeds;
        }
    }

    private sealed class StubChallengePersistence
        : IEmailVerificationChallengePersistence
    {
        public EmailVerificationChallengeConsumptionPersistenceResult ConsumptionResult
        { get; set; } = EmailVerificationChallengeConsumptionPersistenceResult.Rejected;

        public int IssueCallCount { get; private set; }

        public int ConsumeCallCount { get; private set; }

        public EmailVerificationTokenHash? ConsumedTokenHash { get; private set; }

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
            return Task.FromResult(
                EmailVerificationChallengeIssuancePersistenceResult.Rejected);
        }

        public Task<EmailVerificationChallengeConsumptionPersistenceResult>
            TryConsumeAsync(
                EmailVerificationTokenHash tokenHash,
                CancellationToken cancellationToken = default)
        {
            ConsumeCallCount++;
            ConsumedTokenHash = tokenHash;
            CancellationToken = cancellationToken;
            return Task.FromResult(ConsumptionResult);
        }
    }

    private static byte[] CreateHashBytes(byte seed)
    {
        return Enumerable.Range(0, 32)
            .Select(index => (byte)(seed + index))
            .ToArray();
    }
}
