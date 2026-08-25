using System.Reflection;
using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Authorization;

public sealed class OrganizationAdministrationAuthorizationTests
{
    private static readonly Guid UserId = Guid.Parse(
        "8be1418d-49a5-4983-93f7-85992a164db6");
    private static readonly Guid OrganizationId = Guid.Parse(
        "d919bc3f-5b8f-47e2-b38a-77dc8c6bc5c4");
    private static readonly Guid MembershipId = Guid.Parse(
        "8be55287-f213-4496-ad47-c18f8e56dbdc");

    [Theory]
    [InlineData(OrganizationRole.Owner, true, true)]
    [InlineData(OrganizationRole.Administrator, true, false)]
    [InlineData(OrganizationRole.Member, false, false)]
    public async Task AuthorizeAsync_WithSupportedLiveRole_GrantsExpectedReadActions(
        OrganizationRole role,
        bool canViewAdministrationDetails,
        bool canChangeMemberRole)
    {
        var lookup = new StubAccessLookup(
            new OrganizationAccessLookupResult(
                UserId,
                OrganizationId,
                MembershipId,
                role));
        OrganizationAdministrationAuthorization authorization = Create(lookup);

        OrganizationAdministrationAuthorizationResult result =
            await authorization.AuthorizeAsync(UserId, OrganizationId);

        Assert.Equal(
            OrganizationAdministrationAuthorizationStatus.Allowed,
            result.Status);
        Assert.Equal(UserId, result.UserId);
        Assert.Equal(OrganizationId, result.OrganizationId);
        Assert.Equal(MembershipId, result.MembershipId);
        Assert.Equal(role, result.Role);
        Assert.True(result.Allows(OrganizationAdministrationAction.ViewTeam));
        Assert.Equal(
            canViewAdministrationDetails,
            result.Allows(
                OrganizationAdministrationAction.ViewTeamAdministrationDetails));
        Assert.Equal(
            canChangeMemberRole,
            result.Allows(OrganizationAdministrationAction.ChangeMemberRole));
    }

    [Theory]
    [InlineData(LiveAccessVariant.MismatchedUser)]
    [InlineData(LiveAccessVariant.MismatchedOrganization)]
    [InlineData(LiveAccessVariant.MissingMembership)]
    [InlineData(LiveAccessVariant.EmptyMembership)]
    [InlineData(LiveAccessVariant.MissingAccess)]
    [InlineData(LiveAccessVariant.UnsupportedRole)]
    public async Task AuthorizeAsync_WithUntrustedLiveAccess_FailsClosed(
        LiveAccessVariant variant)
    {
        OrganizationAccessLookupResult? access = variant switch
        {
            LiveAccessVariant.MismatchedUser => CreateAccess(
                userId: Guid.Parse("bcfa426d-3e09-4af8-815e-a5c669f0fb67")),
            LiveAccessVariant.MismatchedOrganization => CreateAccess(
                organizationId: Guid.Parse(
                    "03cd2814-0162-4a62-bf07-a6ea02c7febd")),
            LiveAccessVariant.MissingMembership => new OrganizationAccessLookupResult(
                UserId,
                OrganizationId,
                null,
                OrganizationRole.Member),
            LiveAccessVariant.EmptyMembership => new OrganizationAccessLookupResult(
                UserId,
                OrganizationId,
                Guid.Empty,
                OrganizationRole.Member),
            LiveAccessVariant.MissingAccess => null,
            LiveAccessVariant.UnsupportedRole => CreateAccess(
                role: (OrganizationRole)999),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        var lookup = new StubAccessLookup(access);
        OrganizationAdministrationAuthorization authorization = Create(lookup);

        OrganizationAdministrationAuthorizationResult result =
            await authorization.AuthorizeAsync(UserId, OrganizationId);

        Assert.Equal(
            OrganizationAdministrationAuthorizationStatus.Denied,
            result.Status);
        Assert.Null(result.UserId);
        Assert.Null(result.OrganizationId);
        Assert.Null(result.MembershipId);
        Assert.Null(result.Role);
        Assert.False(result.Allows(OrganizationAdministrationAction.ViewTeam));
        Assert.False(result.Allows(
            OrganizationAdministrationAction.ViewTeamAdministrationDetails));
        Assert.False(result.Allows(
            OrganizationAdministrationAction.ChangeMemberRole));
    }

    [Fact]
    public async Task AuthorizeAsync_PropagatesCancellationToLiveLookup()
    {
        using var cancellationSource = new CancellationTokenSource();
        var lookup = new StubAccessLookup(CreateAccess());
        OrganizationAdministrationAuthorization authorization = Create(lookup);

        await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            cancellationSource.Token);

        Assert.Equal(cancellationSource.Token, lookup.CancellationToken);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AuthorizeAsync_WithEmptyContext_DeniesWithoutLookup(
        bool emptyUserId,
        bool emptyOrganizationId)
    {
        var lookup = new StubAccessLookup(CreateAccess());
        OrganizationAdministrationAuthorization authorization = Create(lookup);

        OrganizationAdministrationAuthorizationResult result =
            await authorization.AuthorizeAsync(
                emptyUserId ? Guid.Empty : UserId,
                emptyOrganizationId ? Guid.Empty : OrganizationId);

        Assert.Equal(
            OrganizationAdministrationAuthorizationStatus.Denied,
            result.Status);
        Assert.Equal(0, lookup.CallCount);
    }

    [Fact]
    public void Allows_WithUnsupportedAction_FailsClosed()
    {
        OrganizationAdministrationAuthorizationResult result =
            OrganizationAdministrationAuthorizationResult.Allowed(
                UserId,
                OrganizationId,
                MembershipId,
                OrganizationRole.Owner);

        Assert.False(result.Allows((OrganizationAdministrationAction)999));
    }

    [Fact]
    public void Allowed_RequiresAuthoritativeIdentity()
    {
        MethodInfo allowedFactory = Assert.Single(
            typeof(OrganizationAdministrationAuthorizationResult)
                .GetMethods(BindingFlags.Public | BindingFlags.Static),
            method => method.Name == nameof(
                OrganizationAdministrationAuthorizationResult.Allowed));

        Assert.Equal(
            [typeof(Guid), typeof(Guid), typeof(Guid), typeof(OrganizationRole)],
            allowedFactory
                .GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray());
    }

    private static OrganizationAdministrationAuthorization Create(
        StubAccessLookup lookup)
    {
        return new OrganizationAdministrationAuthorization(
            new OrganizationAccessAuthorization(lookup));
    }

    private static OrganizationAccessLookupResult CreateAccess(
        Guid? userId = null,
        Guid? organizationId = null,
        OrganizationRole role = OrganizationRole.Member)
    {
        return new OrganizationAccessLookupResult(
            userId ?? UserId,
            organizationId ?? OrganizationId,
            MembershipId,
            role);
    }

    public enum LiveAccessVariant
    {
        MismatchedUser,
        MismatchedOrganization,
        MissingMembership,
        EmptyMembership,
        MissingAccess,
        UnsupportedRole
    }

    private sealed class StubAccessLookup(
        OrganizationAccessLookupResult? result) : IOrganizationAccessLookup
    {
        public int CallCount { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(result?.Role);
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CancellationToken = cancellationToken;
            return Task.FromResult(result);
        }
    }
}
