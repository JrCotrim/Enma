using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Authorization;

public sealed class LegalTaskViewAuthorizationTests
{
    private static readonly Guid UserId = Guid.Parse(
        "9a9e442c-0f03-474a-8807-530d253eb90b");
    private static readonly Guid OrganizationId = Guid.Parse(
        "65636dd4-1a4a-453e-aa71-1fd073078266");
    private static readonly Guid MembershipId = Guid.Parse(
        "4c742d44-bf30-4642-a550-e1cbe9e8df90");

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task AuthorizeAsync_WithApprovedLiveRole_AllowsView(
        OrganizationRole role)
    {
        var lookup = new StubAccessLookup(role);
        var authorization = new LegalTaskViewAuthorization(
            new OrganizationAccessAuthorization(lookup));

        LegalTaskViewAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId);

        Assert.Equal(LegalTaskViewAuthorizationStatus.Allowed, result.Status);
        Assert.Equal(UserId, result.UserId);
        Assert.Equal(OrganizationId, result.OrganizationId);
        Assert.Equal(MembershipId, result.MembershipId);
        Assert.Equal(1, lookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithoutLiveAccess_Denies()
    {
        var authorization = new LegalTaskViewAuthorization(
            new OrganizationAccessAuthorization(new StubAccessLookup(null)));

        LegalTaskViewAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId);

        Assert.Same(LegalTaskViewAuthorizationResult.Denied, result);
    }

    [Fact]
    public async Task AuthorizeAsync_WithUndefinedLiveRole_Denies()
    {
        var authorization = new LegalTaskViewAuthorization(
            new OrganizationAccessAuthorization(
                new StubAccessLookup((OrganizationRole)int.MaxValue)));

        LegalTaskViewAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId);

        Assert.Same(LegalTaskViewAuthorizationResult.Denied, result);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AuthorizeAsync_WithEmptyContext_DeniesWithoutLookup(
        bool emptyUserId,
        bool emptyOrganizationId)
    {
        var lookup = new StubAccessLookup(OrganizationRole.Owner);
        var authorization = new LegalTaskViewAuthorization(
            new OrganizationAccessAuthorization(lookup));

        LegalTaskViewAuthorizationResult result = await authorization.AuthorizeAsync(
            emptyUserId ? Guid.Empty : UserId,
            emptyOrganizationId ? Guid.Empty : OrganizationId);

        Assert.Same(LegalTaskViewAuthorizationResult.Denied, result);
        Assert.Equal(0, lookup.CallCount);
    }

    private sealed class StubAccessLookup(OrganizationRole? role)
        : IOrganizationAccessLookup
    {
        public int CallCount { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "The full live access lookup must be used for Task view authorization.");
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            OrganizationAccessLookupResult? result = role.HasValue
                ? new OrganizationAccessLookupResult(
                    userId,
                    organizationId,
                    MembershipId,
                    role.Value)
                : null;
            return Task.FromResult(result);
        }
    }
}
