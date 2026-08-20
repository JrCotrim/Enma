using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Authorization;

public sealed class LegalDocumentReadAuthorizationTests
{
    private static readonly Guid UserId = Guid.Parse(
        "72ab13cc-6a07-45de-a24b-f697698208b9");
    private static readonly Guid OrganizationId = Guid.Parse(
        "e03f4ec3-1f02-4225-8d19-29ef85df45f8");
    private static readonly Guid MembershipId = Guid.Parse(
        "4d229a89-7bc5-4e13-9177-0d247f457835");

    [Theory]
    [InlineData(LegalDocumentReadAction.ListMetadata, OrganizationRole.Owner)]
    [InlineData(
        LegalDocumentReadAction.ListMetadata,
        OrganizationRole.Administrator)]
    [InlineData(LegalDocumentReadAction.ListMetadata, OrganizationRole.Member)]
    [InlineData(LegalDocumentReadAction.ViewMetadata, OrganizationRole.Owner)]
    [InlineData(
        LegalDocumentReadAction.ViewMetadata,
        OrganizationRole.Administrator)]
    [InlineData(LegalDocumentReadAction.ViewMetadata, OrganizationRole.Member)]
    public async Task AuthorizeAsync_WithLiveContext_AllowsExplicitAction(
        LegalDocumentReadAction action,
        OrganizationRole role)
    {
        var lookup = new StubAccessLookup(CreateAccess(role));
        LegalDocumentReadAuthorization authorization =
            CreateAuthorization(lookup);

        LegalDocumentReadAuthorizationResult result =
            await authorization.AuthorizeAsync(
                UserId,
                OrganizationId,
                action);

        Assert.Equal(LegalDocumentReadAuthorizationResult.Allowed, result);
        Assert.Equal(1, lookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithoutLiveAccess_Denies()
    {
        LegalDocumentReadAuthorization authorization =
            CreateAuthorization(new StubAccessLookup(null));

        LegalDocumentReadAuthorizationResult result =
            await authorization.AuthorizeAsync(
                UserId,
                OrganizationId,
                LegalDocumentReadAction.ListMetadata);

        Assert.Equal(LegalDocumentReadAuthorizationResult.Denied, result);
    }

    [Fact]
    public async Task AuthorizeAsync_WithUndefinedLiveRole_Denies()
    {
        LegalDocumentReadAuthorization authorization =
            CreateAuthorization(new StubAccessLookup(
                CreateAccess((OrganizationRole)int.MaxValue)));

        LegalDocumentReadAuthorizationResult result =
            await authorization.AuthorizeAsync(
                UserId,
                OrganizationId,
                LegalDocumentReadAction.ViewMetadata);

        Assert.Equal(LegalDocumentReadAuthorizationResult.Denied, result);
    }

    [Fact]
    public async Task AuthorizeAsync_WithMismatchedContext_Denies()
    {
        var access = new OrganizationAccessLookupResult(
            Guid.NewGuid(),
            OrganizationId,
            MembershipId,
            OrganizationRole.Owner);
        LegalDocumentReadAuthorization authorization =
            CreateAuthorization(new StubAccessLookup(access));

        LegalDocumentReadAuthorizationResult result =
            await authorization.AuthorizeAsync(
                UserId,
                OrganizationId,
                LegalDocumentReadAction.ViewMetadata);

        Assert.Equal(LegalDocumentReadAuthorizationResult.Denied, result);
    }

    [Fact]
    public async Task AuthorizeAsync_WithMissingMembership_Denies()
    {
        var access = new OrganizationAccessLookupResult(
            UserId,
            OrganizationId,
            null,
            OrganizationRole.Owner);
        LegalDocumentReadAuthorization authorization =
            CreateAuthorization(new StubAccessLookup(access));

        LegalDocumentReadAuthorizationResult result =
            await authorization.AuthorizeAsync(
                UserId,
                OrganizationId,
                LegalDocumentReadAction.ListMetadata);

        Assert.Equal(LegalDocumentReadAuthorizationResult.Denied, result);
    }

    [Fact]
    public async Task AuthorizeAsync_WithUndefinedAction_DeniesWithoutLookup()
    {
        var lookup = new StubAccessLookup(
            CreateAccess(OrganizationRole.Owner));
        LegalDocumentReadAuthorization authorization =
            CreateAuthorization(lookup);

        LegalDocumentReadAuthorizationResult result =
            await authorization.AuthorizeAsync(
                UserId,
                OrganizationId,
                (LegalDocumentReadAction)int.MaxValue);

        Assert.Equal(LegalDocumentReadAuthorizationResult.Denied, result);
        Assert.Equal(0, lookup.CallCount);
    }

    private static LegalDocumentReadAuthorization CreateAuthorization(
        IOrganizationAccessLookup lookup)
    {
        return new LegalDocumentReadAuthorization(
            new OrganizationAccessAuthorization(lookup));
    }

    private static OrganizationAccessLookupResult CreateAccess(
        OrganizationRole role)
    {
        return new OrganizationAccessLookupResult(
            UserId,
            OrganizationId,
            MembershipId,
            role);
    }

    private sealed class StubAccessLookup(
        OrganizationAccessLookupResult? access)
        : IOrganizationAccessLookup
    {
        public int CallCount { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "Document reads must use full live access state.");
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(access);
        }
    }
}
