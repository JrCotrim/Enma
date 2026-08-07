using Enma.Domain.Authentication;

namespace Enma.UnitTests.Domain.Authentication;

public sealed class AuthenticationSessionTests
{
    private static readonly Guid UserId =
        Guid.Parse("6c942f46-2889-4df0-b76e-8b06fbe3c4a2");
    private static readonly Guid OrganizationId =
        Guid.Parse("0304942e-74f2-4b83-9397-b1c13c8a4689");
    private static readonly Guid OtherOrganizationId =
        Guid.Parse("7b91e972-2265-45c1-958d-d06e9d35915b");
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        7,
        12,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateTimeOffset IdleExpiresAt = CreatedAt.AddMinutes(30);
    private static readonly DateTimeOffset AbsoluteExpiresAt = CreatedAt.AddHours(2);

    [Fact]
    public void Constructor_WithValidData_InitializesSession()
    {
        AuthenticationSessionSecretHash secretHash = CreateSecretHash();

        var session = new AuthenticationSession(
            UserId,
            secretHash,
            3,
            CreatedAt,
            IdleExpiresAt,
            AbsoluteExpiresAt,
            OrganizationId);

        Assert.NotEqual(Guid.Empty, session.Id);
        Assert.Equal(UserId, session.UserId);
        Assert.Equal(secretHash, session.SecretHash);
        Assert.Equal(3, session.CredentialVersionAtIssue);
        Assert.Equal(OrganizationId, session.SelectedOrganizationId);
        Assert.Equal(CreatedAt, session.CreatedAt);
        Assert.Equal(CreatedAt, session.LastSeenAt);
        Assert.Equal(IdleExpiresAt, session.IdleExpiresAt);
        Assert.Equal(AbsoluteExpiresAt, session.AbsoluteExpiresAt);
        Assert.Null(session.RevokedAt);
        Assert.Equal(1, session.ConcurrencyVersion);
    }

    [Fact]
    public void Constructor_WithEmptyUserId_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreateSession(userId: Guid.Empty));

        Assert.Equal("userId", exception.ParamName);
        Assert.Contains(AuthenticationSessionErrors.UserIdRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithNonPositiveCredentialVersion_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateSession(credentialVersionAtIssue: 0));

        Assert.Equal("credentialVersionAtIssue", exception.ParamName);
        Assert.Contains(
            AuthenticationSessionErrors.CredentialVersionAtIssueInvalid,
            exception.Message);
    }

    [Fact]
    public void Constructor_WithInvalidCreatedAt_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateSession(createdAt: DateTimeOffset.MinValue));

        Assert.Equal("createdAt", exception.ParamName);
        Assert.Contains(AuthenticationSessionErrors.CreatedAtInvalid, exception.Message);
    }

    [Fact]
    public void Constructor_WithIdleExpirationNotAfterCreation_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateSession(idleExpiresAt: CreatedAt));

        Assert.Equal("idleExpiresAt", exception.ParamName);
        Assert.Contains(
            AuthenticationSessionErrors.IdleExpiresAtInvalid,
            exception.Message);
    }

    [Fact]
    public void Constructor_WithAbsoluteExpirationNotAfterCreation_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateSession(absoluteExpiresAt: CreatedAt));

        Assert.Equal("absoluteExpiresAt", exception.ParamName);
        Assert.Contains(
            AuthenticationSessionErrors.AbsoluteExpiresAtInvalid,
            exception.Message);
    }

    [Fact]
    public void Constructor_WithIdleExpirationAfterAbsoluteExpiration_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateSession(idleExpiresAt: AbsoluteExpiresAt.AddTicks(1)));

        Assert.Equal("idleExpiresAt", exception.ParamName);
        Assert.Contains(
            AuthenticationSessionErrors.IdleExpiresAtInvalid,
            exception.Message);
    }

    [Fact]
    public void Constructor_WithEmptySelectedOrganizationId_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreateSession(selectedOrganizationId: Guid.Empty));

        Assert.Equal("selectedOrganizationId", exception.ParamName);
        Assert.Contains(
            AuthenticationSessionErrors.SelectedOrganizationIdInvalid,
            exception.Message);
    }

    [Fact]
    public void Touch_WithValidRenewal_UpdatesActivityAndConcurrencyVersion()
    {
        AuthenticationSession session = CreateSession();
        DateTimeOffset seenAt = CreatedAt.AddMinutes(10);
        DateTimeOffset renewedIdleExpiresAt = IdleExpiresAt.AddMinutes(15);

        session.Touch(seenAt, renewedIdleExpiresAt);

        Assert.Equal(seenAt, session.LastSeenAt);
        Assert.Equal(renewedIdleExpiresAt, session.IdleExpiresAt);
        Assert.Equal(2, session.ConcurrencyVersion);
    }

    [Fact]
    public void Touch_WithEarlierSeenAt_ThrowsAndPreservesState()
    {
        AuthenticationSession session = CreateSession();
        DateTimeOffset firstSeenAt = CreatedAt.AddMinutes(10);
        DateTimeOffset renewedIdleExpiresAt = IdleExpiresAt.AddMinutes(15);
        session.Touch(firstSeenAt, renewedIdleExpiresAt);
        SessionState state = CaptureState(session);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.Touch(
                firstSeenAt.AddTicks(-1),
                renewedIdleExpiresAt.AddMinutes(1)));

        Assert.Equal("seenAt", exception.ParamName);
        Assert.Contains(
            AuthenticationSessionErrors.LastSeenAtCannotMoveBackward,
            exception.Message);
        AssertState(session, state);
    }

    [Fact]
    public void Touch_AtCurrentIdleExpiration_ThrowsAndPreservesState()
    {
        AuthenticationSession session = CreateSession();
        SessionState state = CaptureState(session);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.Touch(IdleExpiresAt, IdleExpiresAt.AddMinutes(10)));

        Assert.Equal("seenAt", exception.ParamName);
        AssertState(session, state);
    }

    [Fact]
    public void Touch_WithIdleExpirationBeyondAbsoluteExpiration_ThrowsAndPreservesState()
    {
        AuthenticationSession session = CreateSession();
        SessionState state = CaptureState(session);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.Touch(
                CreatedAt.AddMinutes(10),
                AbsoluteExpiresAt.AddTicks(1)));

        Assert.Equal("idleExpiresAt", exception.ParamName);
        AssertState(session, state);
    }

    [Fact]
    public void Touch_WithIdleExpirationMovingBackward_ThrowsAndPreservesState()
    {
        AuthenticationSession session = CreateSession();
        SessionState state = CaptureState(session);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.Touch(
                CreatedAt.AddMinutes(10),
                IdleExpiresAt.AddTicks(-1)));

        Assert.Equal("idleExpiresAt", exception.ParamName);
        Assert.Contains(
            AuthenticationSessionErrors.IdleExpiresAtCannotMoveBackward,
            exception.Message);
        AssertState(session, state);
    }

    [Fact]
    public void Touch_WithIdenticalState_IsNoOp()
    {
        AuthenticationSession session = CreateSession();
        SessionState state = CaptureState(session);

        session.Touch(session.LastSeenAt, session.IdleExpiresAt);

        AssertState(session, state);
    }

    [Fact]
    public void Revoke_WithValidTimestamp_RevokesAndIncrementsVersion()
    {
        AuthenticationSession session = CreateSession();
        DateTimeOffset revokedAt = AbsoluteExpiresAt.AddHours(1);

        session.Revoke(revokedAt);

        Assert.Equal(revokedAt, session.RevokedAt);
        Assert.Equal(2, session.ConcurrencyVersion);
    }

    [Fact]
    public void Revoke_WhenAlreadyRevoked_PreservesFirstRevocationAndVersion()
    {
        AuthenticationSession session = CreateSession(
            selectedOrganizationId: OrganizationId);
        DateTimeOffset firstRevokedAt = CreatedAt.AddMinutes(20);
        session.Revoke(firstRevokedAt);
        SessionState revokedState = CaptureState(session);

        session.Revoke(firstRevokedAt.AddMinutes(1));
        AssertState(session, revokedState);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.Revoke(CreatedAt.AddTicks(-1)));
        AssertState(session, revokedState);

        Assert.Throws<InvalidOperationException>(() =>
            session.Touch(
                CreatedAt.AddMinutes(5),
                IdleExpiresAt.AddMinutes(1)));
        Assert.Throws<InvalidOperationException>(() =>
            session.SelectOrganization(OtherOrganizationId));
        Assert.Throws<InvalidOperationException>(session.ClearSelectedOrganization);
        AssertState(session, revokedState);
    }

    [Fact]
    public void SelectOrganization_WithValidOrganization_ChangesSelectionAndVersion()
    {
        AuthenticationSession session = CreateSession();

        session.SelectOrganization(OrganizationId);

        Assert.Equal(OrganizationId, session.SelectedOrganizationId);
        Assert.Equal(2, session.ConcurrencyVersion);

        session.SelectOrganization(OrganizationId);

        Assert.Equal(OrganizationId, session.SelectedOrganizationId);
        Assert.Equal(2, session.ConcurrencyVersion);
    }

    [Fact]
    public void SelectOrganization_WithEmptyOrganization_ThrowsAndPreservesState()
    {
        AuthenticationSession session = CreateSession();
        SessionState state = CaptureState(session);

        var exception = Assert.Throws<ArgumentException>(() =>
            session.SelectOrganization(Guid.Empty));

        Assert.Equal("organizationId", exception.ParamName);
        Assert.Contains(
            AuthenticationSessionErrors.SelectedOrganizationIdInvalid,
            exception.Message);
        AssertState(session, state);
    }

    [Fact]
    public void ClearSelectedOrganization_WhenSelected_ClearsAndIncrementsVersion()
    {
        AuthenticationSession session = CreateSession(
            selectedOrganizationId: OrganizationId);

        session.ClearSelectedOrganization();

        Assert.Null(session.SelectedOrganizationId);
        Assert.Equal(2, session.ConcurrencyVersion);

        session.ClearSelectedOrganization();

        Assert.Null(session.SelectedOrganizationId);
        Assert.Equal(2, session.ConcurrencyVersion);
    }

    private static AuthenticationSession CreateSession(
        Guid? userId = null,
        long credentialVersionAtIssue = 3,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? idleExpiresAt = null,
        DateTimeOffset? absoluteExpiresAt = null,
        Guid? selectedOrganizationId = null)
    {
        return new AuthenticationSession(
            userId ?? UserId,
            CreateSecretHash(),
            credentialVersionAtIssue,
            createdAt ?? CreatedAt,
            idleExpiresAt ?? IdleExpiresAt,
            absoluteExpiresAt ?? AbsoluteExpiresAt,
            selectedOrganizationId);
    }

    private static AuthenticationSessionSecretHash CreateSecretHash()
    {
        byte[] value = Enumerable.Range(1, 32)
            .Select(number => (byte)number)
            .ToArray();

        return new AuthenticationSessionSecretHash(value);
    }

    private static SessionState CaptureState(AuthenticationSession session)
    {
        return new SessionState(
            session.LastSeenAt,
            session.IdleExpiresAt,
            session.RevokedAt,
            session.SelectedOrganizationId,
            session.ConcurrencyVersion);
    }

    private static void AssertState(
        AuthenticationSession session,
        SessionState expected)
    {
        Assert.Equal(expected.LastSeenAt, session.LastSeenAt);
        Assert.Equal(expected.IdleExpiresAt, session.IdleExpiresAt);
        Assert.Equal(expected.RevokedAt, session.RevokedAt);
        Assert.Equal(expected.SelectedOrganizationId, session.SelectedOrganizationId);
        Assert.Equal(expected.ConcurrencyVersion, session.ConcurrencyVersion);
    }

    private sealed record SessionState(
        DateTimeOffset LastSeenAt,
        DateTimeOffset IdleExpiresAt,
        DateTimeOffset? RevokedAt,
        Guid? SelectedOrganizationId,
        long ConcurrencyVersion);
}
