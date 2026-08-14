using System.Reflection;
using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Authorization;

public sealed class OrganizationAccessAuthorizationTests
{
    private static readonly Guid UserId = Guid.Parse(
        "88e19788-d6dd-46db-92a2-8d12e790c140");

    private static readonly Guid OrganizationId = Guid.Parse(
        "962ac643-fe5c-4dcf-94b9-445b8a9e16cb");

    [Fact]
    public async Task AuthorizeAsync_WithNoActiveMembership_ReturnsDenied()
    {
        var lookup = new StubOrganizationAccessLookup(null);
        var authorization = new OrganizationAccessAuthorization(lookup);

        OrganizationAccessAuthorizationResult result =
            await authorization.AuthorizeAsync(UserId, OrganizationId);

        Assert.Equal(OrganizationAccessAuthorizationStatus.Denied, result.Status);
        Assert.Null(result.UserId);
        Assert.Null(result.OrganizationId);
        Assert.Null(result.MembershipId);
        Assert.Null(result.Role);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task AuthorizeAsync_WithActiveMembership_ReturnsAllowedWithLookupRole(
        OrganizationRole role)
    {
        var lookup = new StubOrganizationAccessLookup(role);
        var authorization = new OrganizationAccessAuthorization(lookup);

        OrganizationAccessAuthorizationResult result =
            await authorization.AuthorizeAsync(UserId, OrganizationId);

        Assert.Equal(OrganizationAccessAuthorizationStatus.Allowed, result.Status);
        Assert.Equal(UserId, result.UserId);
        Assert.Equal(OrganizationId, result.OrganizationId);
        Assert.Null(result.MembershipId);
        Assert.Equal(role, result.Role);
    }

    [Fact]
    public async Task AuthorizeAsync_WithContextualIds_PassesOnlyThoseIdsToLiveLookup()
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Member);
        var authorization = new OrganizationAccessAuthorization(lookup);

        await authorization.AuthorizeAsync(UserId, OrganizationId);

        Assert.Equal(1, lookup.CallCount);
        Assert.Equal(UserId, lookup.UserId);
        Assert.Equal(OrganizationId, lookup.OrganizationId);
    }

    [Fact]
    public void PublicContract_ContainsOnlyLiveLookupAndContextualIdentifiers()
    {
        ConstructorInfo constructor = Assert.Single(
            typeof(OrganizationAccessAuthorization).GetConstructors());
        MethodInfo authorizeMethod = Assert.Single(
            typeof(OrganizationAccessAuthorization).GetMethods(),
            method => method.Name == "AuthorizeAsync");

        Assert.Equal(
            [typeof(IOrganizationAccessLookup)],
            constructor.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray());
        Assert.Equal(
            [typeof(Guid), typeof(Guid), typeof(CancellationToken)],
            authorizeMethod.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray());
    }

    [Fact]
    public void DeniedResult_ExposesNoExistenceOrMembershipReason()
    {
        PropertyInfo[] publicProperties =
            typeof(OrganizationAccessAuthorizationResult)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.Equal(
            [nameof(OrganizationAccessAuthorizationResult.MembershipId),
                nameof(OrganizationAccessAuthorizationResult.OrganizationId),
                nameof(OrganizationAccessAuthorizationResult.Role),
                nameof(OrganizationAccessAuthorizationResult.Status),
                nameof(OrganizationAccessAuthorizationResult.UserId)],
            publicProperties
                .Select(property => property.Name)
                .OrderBy(name => name)
                .ToArray());
        Assert.Equal(
            OrganizationAccessAuthorizationStatus.Denied,
            OrganizationAccessAuthorizationResult.Denied.Status);
        Assert.Null(OrganizationAccessAuthorizationResult.Denied.UserId);
        Assert.Null(OrganizationAccessAuthorizationResult.Denied.OrganizationId);
        Assert.Null(OrganizationAccessAuthorizationResult.Denied.MembershipId);
        Assert.Null(OrganizationAccessAuthorizationResult.Denied.Role);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AuthorizeAsync_WithEmptyIdentifier_ReturnsDeniedWithoutLookup(
        bool emptyUserId,
        bool emptyOrganizationId)
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        var authorization = new OrganizationAccessAuthorization(lookup);

        OrganizationAccessAuthorizationResult result =
            await authorization.AuthorizeAsync(
                emptyUserId ? Guid.Empty : UserId,
                emptyOrganizationId ? Guid.Empty : OrganizationId);

        Assert.Equal(OrganizationAccessAuthorizationStatus.Denied, result.Status);
        Assert.Null(result.UserId);
        Assert.Null(result.OrganizationId);
        Assert.Null(result.MembershipId);
        Assert.Null(result.Role);
        Assert.Equal(0, lookup.CallCount);
    }

    private sealed class StubOrganizationAccessLookup(
        OrganizationRole? role) : IOrganizationAccessLookup
    {
        public int CallCount { get; private set; }

        public Guid? UserId { get; private set; }

        public Guid? OrganizationId { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            UserId = userId;
            OrganizationId = organizationId;

            return Task.FromResult(role);
        }
    }
}
