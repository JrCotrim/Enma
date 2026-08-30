using Enma.Application.Organizations.Invitations;
using Enma.Domain.Organizations;
using Enma.Domain.Users;

namespace Enma.UnitTests.Domain.Organizations;

public sealed class OrganizationInvitationTests
{
    private static readonly Guid OrganizationId = Guid.Parse(
        "10000000-0000-0000-0000-000000000001");
    private static readonly Guid CreatorMembershipId = Guid.Parse(
        "20000000-0000-0000-0000-000000000001");
    private static readonly Guid AcceptedByUserId = Guid.Parse(
        "30000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        30,
        12,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateTimeOffset TokenIssuedAt =
        CreatedAt.AddMinutes(1);
    private static readonly DateTimeOffset ExpiresAt =
        TokenIssuedAt.Add(OrganizationInvitationPolicy.TokenLifetime);

    [Theory]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public void Constructor_WithAllowedRole_InitializesPendingInvitation(
        OrganizationRole role)
    {
        OrganizationInvitationTokenHash tokenHash = CreateTokenHash(1);

        var invitation = new OrganizationInvitation(
            OrganizationId,
            "  INVITED.USER@EXAMPLE.TEST  ",
            role,
            CreatorMembershipId,
            tokenHash,
            CreatedAt,
            TokenIssuedAt,
            ExpiresAt);

        Assert.NotEqual(Guid.Empty, invitation.Id);
        Assert.Equal(OrganizationId, invitation.OrganizationId);
        Assert.Equal(
            User.NormalizeEmail("  INVITED.USER@EXAMPLE.TEST  "),
            invitation.InvitedEmail);
        Assert.Equal(role, invitation.Role);
        Assert.Equal(CreatorMembershipId, invitation.CreatedByMembershipId);
        Assert.Same(tokenHash, invitation.TokenHash);
        Assert.Equal(CreatedAt, invitation.CreatedAt);
        Assert.Equal(TokenIssuedAt, invitation.TokenIssuedAt);
        Assert.Equal(ExpiresAt, invitation.ExpiresAt);
        Assert.Null(invitation.AcceptedAt);
        Assert.Null(invitation.AcceptedByUserId);
        Assert.Null(invitation.RevokedAt);
        Assert.Null(invitation.ExpiredAt);
        Assert.Equal(
            OrganizationInvitationState.Pending,
            invitation.GetState(TokenIssuedAt));
    }

    [Fact]
    public void Policy_DefinesSevenDayTokenLifetime()
    {
        Assert.Equal(TimeSpan.FromDays(7), OrganizationInvitationPolicy.TokenLifetime);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData((OrganizationRole)0)]
    [InlineData((OrganizationRole)4)]
    public void Constructor_WithDisallowedRole_Throws(OrganizationRole role)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateInvitation(role: role));

        Assert.Equal("role", exception.ParamName);
        Assert.Contains(OrganizationInvitationErrors.RoleInvalid, exception.Message);
    }

    [Theory]
    [InlineData("   ", UserErrors.EmailRequired)]
    [InlineData("invalid-email", UserErrors.EmailInvalidFormat)]
    public void Constructor_WithInvalidEmail_UsesCanonicalUserValidation(
        string email,
        string expectedError)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreateInvitation(invitedEmail: email));

        Assert.Equal("email", exception.ParamName);
        Assert.Contains(expectedError, exception.Message);
    }

    [Fact]
    public void Constructor_WithNullTokenHash_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new OrganizationInvitation(
                OrganizationId,
                "invited@example.test",
                OrganizationRole.Member,
                CreatorMembershipId,
                null!,
                CreatedAt,
                TokenIssuedAt,
                ExpiresAt));

        Assert.Equal("tokenHash", exception.ParamName);
        Assert.Contains(
            OrganizationInvitationErrors.TokenHashRequired,
            exception.Message);
    }

    [Fact]
    public void Constructor_WithTokenIssuedBeforeCreation_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateInvitation(tokenIssuedAt: CreatedAt.AddTicks(-1)));

        Assert.Equal("tokenIssuedAt", exception.ParamName);
        Assert.Contains(
            OrganizationInvitationErrors.TokenIssuedAtInvalid,
            exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithExpirationNotAfterIssue_Throws(int tickOffset)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateInvitation(expiresAt: TokenIssuedAt.AddTicks(tickOffset)));

        Assert.Equal("expiresAt", exception.ParamName);
        Assert.Contains(
            OrganizationInvitationErrors.ExpiresAtInvalid,
            exception.Message);
    }

    [Theory]
    [InlineData(-1, OrganizationInvitationState.Pending)]
    [InlineData(0, OrganizationInvitationState.Expired)]
    [InlineData(1, OrganizationInvitationState.Expired)]
    public void GetState_UsesExpirationBoundaryWithoutMaterialization(
        int tickOffset,
        OrganizationInvitationState expected)
    {
        OrganizationInvitation invitation = CreateInvitation();

        OrganizationInvitationState state = invitation.GetState(
            ExpiresAt.AddTicks(tickOffset));

        Assert.Equal(expected, state);
        Assert.Null(invitation.ExpiredAt);
        Assert.NotNull(invitation.TokenHash);
    }

    [Fact]
    public void Accept_WithValidData_ClearsTokenAndSetsCoherentTerminalState()
    {
        OrganizationInvitation invitation = CreateInvitation();
        DateTimeOffset acceptedAt = TokenIssuedAt.AddHours(1);

        invitation.Accept(AcceptedByUserId, acceptedAt);

        Assert.Equal(acceptedAt, invitation.AcceptedAt);
        Assert.Equal(AcceptedByUserId, invitation.AcceptedByUserId);
        Assert.Null(invitation.TokenHash);
        Assert.Equal(
            OrganizationInvitationState.Accepted,
            invitation.GetState(acceptedAt));
    }

    [Fact]
    public void Accept_WithEmptyUserId_ThrowsAndPreservesPendingState()
    {
        OrganizationInvitation invitation = CreateInvitation();
        OrganizationInvitationTokenHash originalHash = invitation.TokenHash!;

        var exception = Assert.Throws<ArgumentException>(() =>
            invitation.Accept(Guid.Empty, TokenIssuedAt.AddHours(1)));

        Assert.Equal("acceptedByUserId", exception.ParamName);
        Assert.Null(invitation.AcceptedAt);
        Assert.Null(invitation.AcceptedByUserId);
        Assert.Same(originalHash, invitation.TokenHash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Accept_AtOrAfterExpiration_Throws(int tickOffset)
    {
        OrganizationInvitation invitation = CreateInvitation();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            invitation.Accept(
                AcceptedByUserId,
                ExpiresAt.AddTicks(tickOffset)));

        Assert.Equal("acceptedAt", exception.ParamName);
        Assert.Null(invitation.AcceptedAt);
        Assert.Null(invitation.AcceptedByUserId);
        Assert.NotNull(invitation.TokenHash);
    }

    [Fact]
    public void Revoke_WithValidData_ClearsTokenAndSetsTerminalState()
    {
        OrganizationInvitation invitation = CreateInvitation();
        DateTimeOffset revokedAt = TokenIssuedAt.AddHours(1);

        invitation.Revoke(revokedAt);

        Assert.Equal(revokedAt, invitation.RevokedAt);
        Assert.Null(invitation.TokenHash);
        Assert.Equal(
            OrganizationInvitationState.Revoked,
            invitation.GetState(revokedAt));
    }

    [Fact]
    public void Expire_WhenObservedAtBoundary_MaterializesExpiresAtAndClearsToken()
    {
        OrganizationInvitation invitation = CreateInvitation();

        invitation.Expire(ExpiresAt);

        Assert.Equal(invitation.ExpiresAt, invitation.ExpiredAt);
        Assert.Null(invitation.TokenHash);
        Assert.Equal(
            OrganizationInvitationState.Expired,
            invitation.GetState(ExpiresAt));
    }

    [Fact]
    public void Expire_BeforeBoundary_ThrowsAndPreservesPendingState()
    {
        OrganizationInvitation invitation = CreateInvitation();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            invitation.Expire(ExpiresAt.AddTicks(-1)));

        Assert.Equal("observedAt", exception.ParamName);
        Assert.Null(invitation.ExpiredAt);
        Assert.NotNull(invitation.TokenHash);
    }

    [Fact]
    public void RotateToken_WhilePending_RotatesOnlyIssuanceState()
    {
        OrganizationInvitation invitation = CreateInvitation();
        Guid originalId = invitation.Id;
        OrganizationInvitationTokenHash newHash = CreateTokenHash(2);
        DateTimeOffset newIssuedAt = TokenIssuedAt.AddHours(1);
        DateTimeOffset newExpiresAt =
            newIssuedAt.Add(OrganizationInvitationPolicy.TokenLifetime);

        invitation.RotateToken(newHash, newIssuedAt, newExpiresAt);

        Assert.Equal(originalId, invitation.Id);
        Assert.Equal(OrganizationId, invitation.OrganizationId);
        Assert.Equal(CreatorMembershipId, invitation.CreatedByMembershipId);
        Assert.Equal("invited@example.test", invitation.InvitedEmail);
        Assert.Equal(OrganizationRole.Member, invitation.Role);
        Assert.Equal(CreatedAt, invitation.CreatedAt);
        Assert.Same(newHash, invitation.TokenHash);
        Assert.Equal(newIssuedAt, invitation.TokenIssuedAt);
        Assert.Equal(newExpiresAt, invitation.ExpiresAt);
        Assert.Equal(
            OrganizationInvitationState.Pending,
            invitation.GetState(newIssuedAt));
    }

    [Fact]
    public void RotateToken_WithSameHash_ThrowsAndPreservesIssuanceState()
    {
        OrganizationInvitation invitation = CreateInvitation();
        DateTimeOffset originalIssuedAt = invitation.TokenIssuedAt;
        DateTimeOffset originalExpiresAt = invitation.ExpiresAt;

        var exception = Assert.Throws<ArgumentException>(() =>
            invitation.RotateToken(
                new OrganizationInvitationTokenHash(
                    invitation.TokenHash!.ToArray()),
                TokenIssuedAt.AddMinutes(1),
                ExpiresAt.AddMinutes(1)));

        Assert.Equal("tokenHash", exception.ParamName);
        Assert.Equal(originalIssuedAt, invitation.TokenIssuedAt);
        Assert.Equal(originalExpiresAt, invitation.ExpiresAt);
    }

    [Fact]
    public void RotateToken_WhenAlreadyExpired_ThrowsWithoutReopening()
    {
        OrganizationInvitation invitation = CreateInvitation();
        OrganizationInvitationTokenHash originalHash = invitation.TokenHash!;

        Assert.Throws<InvalidOperationException>(() =>
            invitation.RotateToken(
                CreateTokenHash(2),
                ExpiresAt,
                ExpiresAt.Add(OrganizationInvitationPolicy.TokenLifetime)));

        Assert.Equal(
            OrganizationInvitationState.Expired,
            invitation.GetState(ExpiresAt));
        Assert.Same(originalHash, invitation.TokenHash);
    }

    [Theory]
    [InlineData(OrganizationInvitationState.Accepted)]
    [InlineData(OrganizationInvitationState.Revoked)]
    [InlineData(OrganizationInvitationState.Expired)]
    public void TerminalInvitation_RejectsFurtherTransitionsAndResend(
        OrganizationInvitationState terminalState)
    {
        OrganizationInvitation invitation = CreateInvitation();
        MaterializeTerminalState(invitation, terminalState);

        Assert.Throws<InvalidOperationException>(() =>
            invitation.Accept(AcceptedByUserId, TokenIssuedAt.AddHours(2)));
        Assert.Throws<InvalidOperationException>(() =>
            invitation.Revoke(TokenIssuedAt.AddHours(2)));
        Assert.Throws<InvalidOperationException>(() =>
            invitation.Expire(ExpiresAt.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() =>
            invitation.RotateToken(
                CreateTokenHash(2),
                TokenIssuedAt.AddHours(2),
                ExpiresAt.AddHours(2)));
        Assert.Equal(terminalState, invitation.GetState(ExpiresAt.AddHours(1)));
        Assert.Null(invitation.TokenHash);
    }

    private static OrganizationInvitation CreateInvitation(
        string invitedEmail = "invited@example.test",
        OrganizationRole role = OrganizationRole.Member,
        OrganizationInvitationTokenHash? tokenHash = null,
        DateTimeOffset? tokenIssuedAt = null,
        DateTimeOffset? expiresAt = null)
    {
        return new OrganizationInvitation(
            OrganizationId,
            invitedEmail,
            role,
            CreatorMembershipId,
            tokenHash ?? CreateTokenHash(1),
            CreatedAt,
            tokenIssuedAt ?? TokenIssuedAt,
            expiresAt ?? ExpiresAt);
    }

    private static OrganizationInvitationTokenHash CreateTokenHash(byte seed)
    {
        return new OrganizationInvitationTokenHash(
            Enumerable.Range(seed, 32)
                .Select(value => (byte)value)
                .ToArray());
    }

    private static void MaterializeTerminalState(
        OrganizationInvitation invitation,
        OrganizationInvitationState state)
    {
        switch (state)
        {
            case OrganizationInvitationState.Accepted:
                invitation.Accept(AcceptedByUserId, TokenIssuedAt.AddHours(1));
                break;
            case OrganizationInvitationState.Revoked:
                invitation.Revoke(TokenIssuedAt.AddHours(1));
                break;
            case OrganizationInvitationState.Expired:
                invitation.Expire(ExpiresAt);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }
}
