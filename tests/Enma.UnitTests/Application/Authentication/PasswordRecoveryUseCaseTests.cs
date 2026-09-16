using Enma.Application.Authentication;
using Enma.Application.Security;
using Enma.Application.Validation;
using Enma.Domain.Authentication;

namespace Enma.UnitTests.Application.Authentication;

public sealed class PasswordRecoveryUseCaseTests
{
    private const string RawToken = "abcdefghijklmnopqrstuvwxyz0123456789_-ABCDE";

    [Fact]
    public async Task Request_NormalizesEmailAndDeliveryFailureRemainsOpaque()
    {
        var tokenService = new StubTokenService();
        var persistence = new StubPersistence
        {
            IssuanceResult = PasswordRecoveryChallengeIssuanceResult
                .CreateSucceeded("user@example.test")
        };
        var delivery = new StubDelivery();
        var useCase = new RequestPasswordRecoveryUseCase(
            tokenService,
            persistence,
            delivery);

        await useCase.ExecuteAsync("  USER@EXAMPLE.TEST ");

        Assert.Equal("user@example.test", persistence.Email);
        Assert.Equal(PasswordRecoveryPolicy.TokenLifetime, persistence.TokenLifetime);
        Assert.Equal(PasswordRecoveryPolicy.ResendCooldown, persistence.ResendCooldown);
        Assert.Equal(1, delivery.CallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    public async Task Request_MalformedEmailDoesNotIssue(string? email)
    {
        var tokenService = new StubTokenService();
        var persistence = new StubPersistence();
        var delivery = new StubDelivery();
        var useCase = new RequestPasswordRecoveryUseCase(
            tokenService,
            persistence,
            delivery);

        await useCase.ExecuteAsync(email);

        Assert.Equal(0, tokenService.GenerateCallCount);
        Assert.Equal(0, persistence.IssueCallCount);
        Assert.Equal(0, delivery.CallCount);
    }

    [Fact]
    public async Task Reset_ValidPasswordUsesPolicyCompromiseCheckHasherAndPersistence()
    {
        var tokenService = new StubTokenService { HashSucceeds = true };
        var policy = new StubPasswordPolicy();
        var checker = new StubCompromisedPasswordChecker();
        var hasher = new StubPasswordHasher();
        var persistence = new StubPersistence
        {
            ResetResult = PasswordRecoveryResetPersistenceResult.Succeeded
        };
        var useCase = new ResetPasswordUseCase(
            tokenService,
            policy,
            checker,
            hasher,
            persistence);

        ResetPasswordResult result = await useCase.ExecuteAsync(
            RawToken,
            "new synthetic password");

        Assert.Equal(ResetPasswordResult.Succeeded, result);
        Assert.Equal("new synthetic password", policy.Password);
        Assert.Equal("new synthetic password", checker.Password);
        Assert.Equal("new synthetic password", hasher.Password);
        Assert.Equal("new synthetic password", persistence.Password);
        Assert.Equal("new-hash", persistence.PasswordHash);
    }

    [Fact]
    public async Task Reset_CurrentPasswordReuseReturnsSpecificResult()
    {
        var persistence = new StubPersistence
        {
            ResetResult = PasswordRecoveryResetPersistenceResult.CurrentPasswordReuse
        };
        var useCase = new ResetPasswordUseCase(
            new StubTokenService { HashSucceeds = true },
            new StubPasswordPolicy(),
            new StubCompromisedPasswordChecker(),
            new StubPasswordHasher(),
            persistence);

        ResetPasswordResult result = await useCase.ExecuteAsync(
            RawToken,
            "current synthetic password");

        Assert.Equal(ResetPasswordResult.CurrentPasswordReuse, result);
    }

    [Fact]
    public async Task Reset_MalformedTokenStopsBeforePasswordProcessing()
    {
        var policy = new StubPasswordPolicy();
        var checker = new StubCompromisedPasswordChecker();
        var hasher = new StubPasswordHasher();
        var persistence = new StubPersistence();
        var useCase = new ResetPasswordUseCase(
            new StubTokenService(),
            policy,
            checker,
            hasher,
            persistence);

        ResetPasswordResult result = await useCase.ExecuteAsync(
            "bad-token",
            "new synthetic password");

        Assert.Equal(ResetPasswordResult.Invalid, result);
        Assert.Null(policy.Password);
        Assert.Equal(0, persistence.ResetCallCount);
    }

    [Fact]
    public async Task Reset_WeakOrCompromisedPasswordNeverPersists()
    {
        var persistence = new StubPersistence();
        var weakUseCase = new ResetPasswordUseCase(
            new StubTokenService { HashSucceeds = true },
            new StubPasswordPolicy { Reject = true },
            new StubCompromisedPasswordChecker(),
            new StubPasswordHasher(),
            persistence);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            weakUseCase.ExecuteAsync(RawToken, "weak"));

        var compromisedUseCase = new ResetPasswordUseCase(
            new StubTokenService { HashSucceeds = true },
            new StubPasswordPolicy(),
            new StubCompromisedPasswordChecker { IsCompromised = true },
            new StubPasswordHasher(),
            persistence);

        await Assert.ThrowsAsync<CompromisedPasswordException>(() =>
            compromisedUseCase.ExecuteAsync(RawToken, "compromised password"));
        Assert.Equal(0, persistence.ResetCallCount);
    }

    private sealed class StubTokenService : IPasswordRecoveryTokenService
    {
        public PasswordRecoveryTokenHash TokenHash { get; } = new(new byte[32]);
        public bool HashSucceeds { get; set; }
        public int GenerateCallCount { get; private set; }

        public string GenerateToken(out PasswordRecoveryTokenHash tokenHash)
        {
            GenerateCallCount++;
            tokenHash = TokenHash;
            return RawToken;
        }

        public bool TryHashToken(string? rawToken, out PasswordRecoveryTokenHash? tokenHash)
        {
            tokenHash = HashSucceeds ? TokenHash : null;
            return HashSucceeds;
        }
    }

    private sealed class StubPersistence : IPasswordRecoveryPersistence
    {
        public PasswordRecoveryChallengeIssuanceResult IssuanceResult { get; set; } =
            PasswordRecoveryChallengeIssuanceResult.Rejected;
        public PasswordRecoveryResetPersistenceResult ResetResult { get; set; } =
            PasswordRecoveryResetPersistenceResult.Rejected;
        public int IssueCallCount { get; private set; }
        public int ResetCallCount { get; private set; }
        public string? Email { get; private set; }
        public string? Password { get; private set; }
        public string? PasswordHash { get; private set; }
        public TimeSpan TokenLifetime { get; private set; }
        public TimeSpan ResendCooldown { get; private set; }

        public Task<PasswordRecoveryChallengeIssuanceResult> TryIssueOrRotateAsync(
            string normalizedEmail,
            PasswordRecoveryTokenHash tokenHash,
            TimeSpan tokenLifetime,
            TimeSpan resendCooldown,
            CancellationToken cancellationToken = default)
        {
            IssueCallCount++;
            Email = normalizedEmail;
            TokenLifetime = tokenLifetime;
            ResendCooldown = resendCooldown;
            return Task.FromResult(IssuanceResult);
        }

        public Task<PasswordRecoveryResetPersistenceResult> TryResetPasswordAsync(
            PasswordRecoveryTokenHash tokenHash,
            string newPassword,
            string newPasswordHash,
            CancellationToken cancellationToken = default)
        {
            ResetCallCount++;
            Password = newPassword;
            PasswordHash = newPasswordHash;
            return Task.FromResult(ResetResult);
        }
    }

    private sealed class StubDelivery : IPasswordRecoveryDelivery
    {
        public int CallCount { get; private set; }

        public Task<PasswordRecoveryDeliveryResult> DeliverAsync(
            string email,
            string rawToken,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(PasswordRecoveryDeliveryResult.Failed);
        }
    }

    private sealed class StubPasswordPolicy : IPasswordPolicy
    {
        public bool Reject { get; set; }
        public string? Password { get; private set; }

        public void Validate(string password)
        {
            Password = password;
            if (Reject) throw new ArgumentException("Invalid password.", nameof(password));
        }
    }

    private sealed class StubCompromisedPasswordChecker : ICompromisedPasswordChecker
    {
        public bool IsCompromised { get; set; }
        public string? Password { get; private set; }

        public Task<bool> IsCompromisedAsync(
            string password,
            CancellationToken cancellationToken = default)
        {
            Password = password;
            return Task.FromResult(IsCompromised);
        }
    }

    private sealed class StubPasswordHasher : IPasswordHasher
    {
        public string? Password { get; private set; }

        public string HashPassword(string password)
        {
            Password = password;
            return "new-hash";
        }

        public PasswordVerificationResult VerifyHashedPassword(
            string passwordHash,
            string providedPassword) => PasswordVerificationResult.Failed;
    }
}
