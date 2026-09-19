using Enma.Application.Authentication;
using Enma.Application.Organizations.Invitations;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Authentication;

public sealed class ExternalAuthenticationUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "e345e107-96e3-43d6-b67d-c46c5e522806");
    private static readonly DateTimeOffset Now =
        new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, "user@example.test", true)]
    [InlineData("subject", null, true)]
    [InlineData("subject", "user@example.test", false)]
    public async Task Complete_InvalidOrUnverifiedProviderIdentity_FailsBeforePersistence(
        string? subject,
        string? email,
        bool verified)
    {
        var dependencies = new Dependencies();

        ExternalAuthenticationResult result = await dependencies.UseCase.CompleteAsync(
            "Google",
            subject,
            email,
            verified,
            "Google User",
            invitationToken: null);

        Assert.Equal(ExternalAuthenticationStatus.InvalidProviderIdentity, result.Status);
        Assert.Equal(0, dependencies.Persistence.CompleteCalls);
        Assert.Equal(0, dependencies.SessionPersistence.Calls);
    }

    [Fact]
    public async Task Complete_Success_IssuesNormalVersionBoundSession()
    {
        var dependencies = new Dependencies();

        ExternalAuthenticationResult result = await dependencies.UseCase.CompleteAsync(
            "Google",
            "stable-subject",
            "USER@EXAMPLE.TEST",
            emailVerified: true,
            "Google User",
            invitationToken: null);

        Assert.Equal(ExternalAuthenticationStatus.Succeeded, result.Status);
        Assert.Equal("raw-session", result.SessionHandle);
        Assert.Equal("user@example.test", dependencies.Persistence.Email);
        AuthenticationSession session = Assert.IsType<AuthenticationSession>(
            dependencies.SessionPersistence.Session);
        Assert.Equal(UserId, session.UserId);
        Assert.Equal(7, session.CredentialVersionAtIssue);
    }

    [Fact]
    public async Task Complete_MissingProfileName_DelegatesIdentityDecision()
    {
        var dependencies = new Dependencies();
        dependencies.Persistence.ResultStatus =
            ExternalAuthenticationPersistenceStatus.ProfileRequired;

        ExternalAuthenticationResult result = await dependencies.UseCase.CompleteAsync(
            "Google",
            "stable-subject",
            "user@example.test",
            emailVerified: true,
            name: null,
            invitationToken: null);

        Assert.Equal(ExternalAuthenticationStatus.ProfileRequired, result.Status);
        Assert.Equal(1, dependencies.Persistence.CompleteCalls);
        Assert.Equal(0, dependencies.SessionPersistence.Calls);
    }

    private sealed class Dependencies
    {
        public Dependencies()
        {
            UseCase = new ExternalAuthenticationUseCase(
                Persistence,
                new FakeInvitationTokenService(),
                new FakeSessionHandleService(),
                SessionPersistence,
                new FixedTimeProvider(Now));
        }

        public FakeExternalPersistence Persistence { get; } = new();
        public FakeSessionPersistence SessionPersistence { get; } = new();
        public ExternalAuthenticationUseCase UseCase { get; }
    }

    private sealed class FakeExternalPersistence : IExternalAuthenticationPersistence
    {
        public int CompleteCalls { get; private set; }
        public string? Email { get; private set; }
        public ExternalAuthenticationPersistenceStatus ResultStatus { get; set; } =
            ExternalAuthenticationPersistenceStatus.Succeeded;

        public Task<ExternalAuthenticationPersistenceResult> CompleteAsync(
            string provider,
            string providerSubject,
            string normalizedEmail,
            string? name,
            OrganizationInvitationTokenHash? invitationTokenHash,
            CancellationToken cancellationToken = default)
        {
            CompleteCalls++;
            Email = normalizedEmail;
            return Task.FromResult(new ExternalAuthenticationPersistenceResult(
                ResultStatus,
                ResultStatus == ExternalAuthenticationPersistenceStatus.Succeeded
                    ? UserId
                    : null,
                ResultStatus == ExternalAuthenticationPersistenceStatus.Succeeded
                    ? 7
                    : null));
        }

        public Task<bool> LinkAsync(
            Guid userId,
            string provider,
            string providerSubject,
            string normalizedEmail,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeInvitationTokenService
        : IOrganizationInvitationTokenService
    {
        public string GenerateToken(out OrganizationInvitationTokenHash tokenHash)
        {
            tokenHash = new(new byte[32]);
            return "token";
        }

        public bool TryHashToken(
            string? rawToken,
            out OrganizationInvitationTokenHash? tokenHash)
        {
            tokenHash = rawToken is null ? null : new(new byte[32]);
            return rawToken is not null;
        }
    }

    private sealed class FakeSessionHandleService
        : IAuthenticationSessionHandleService
    {
        public string GenerateHandle(out AuthenticationSessionSecretHash secretHash)
        {
            secretHash = new(new byte[32]);
            return "raw-session";
        }

        public bool TryHashHandle(
            string? rawHandle,
            out AuthenticationSessionSecretHash? secretHash)
        {
            secretHash = null;
            return false;
        }
    }

    private sealed class FakeSessionPersistence
        : IAuthenticationSessionIssuancePersistence
    {
        public int Calls { get; private set; }
        public AuthenticationSession? Session { get; private set; }

        public Task<AuthenticationSessionIssuancePersistenceResult> TryPersistAsync(
            AuthenticationSession session,
            string? upgradedPasswordHash,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Session = session;
            return Task.FromResult(
                AuthenticationSessionIssuancePersistenceResult.Succeeded);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
