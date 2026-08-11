using System.Security.Claims;
using Enma.Api.Authorization;
using Enma.Application.Authorization;
using Enma.Domain.Organizations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Enma.IntegrationTests.Api.Authorization;

public sealed class OrganizationAccessAuthorizationHandlerTests
{
    private static readonly Guid UserId = Guid.Parse(
        "3e69dc82-9b9e-4fae-a59d-42aa032b04e6");

    private static readonly Guid OrganizationId = Guid.Parse(
        "dfd060dd-bd61-4ca2-88a1-5d67d3bdf0c8");

    public static TheoryData<string[]> UntrustedUserIdentifiers => new()
    {
        Array.Empty<string>(),
        new[] { "not-a-guid" },
        new[] { Guid.Empty.ToString("D") },
        new[] { UserId.ToString("N") },
        new[] { UserId.ToString("D"), UserId.ToString("D") },
        new[] { UserId.ToString("D"), "78eb13dc-f65a-46c4-8dda-6595ed3614b9" }
    };

    public static TheoryData<bool, object?> InvalidOrganizationContexts => new()
    {
        { false, null },
        { true, "not-a-guid" },
        { true, Guid.Empty.ToString("D") }
    };

    [Fact]
    public async Task HandleAsync_MissingHttpContextResource_DoesNotAuthorizeOrInvokeLiveAuthorization()
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Member);
        var handler = CreateHandler(lookup);
        AuthorizationHandlerContext context = CreateContext(
            CreatePrincipal(UserId.ToString("D")),
            new object());

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Equal(0, lookup.CallCount);
    }

    [Fact]
    public async Task HandleAsync_AnonymousPrincipal_DoesNotAuthorizeOrInvokeLiveAuthorization()
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Member);
        var handler = CreateHandler(lookup);
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, UserId.ToString("D"))]);
        HttpContext httpContext = CreateHttpContext(OrganizationId.ToString("D"));
        AuthorizationHandlerContext context = CreateContext(
            new ClaimsPrincipal(identity),
            httpContext);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Equal(0, lookup.CallCount);
    }

    [Theory]
    [MemberData(nameof(UntrustedUserIdentifiers))]
    public async Task HandleAsync_UntrustedUserIdentifier_DoesNotAuthorizeOrInvokeLiveAuthorization(
        string[] identifiers)
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Member);
        var handler = CreateHandler(lookup);
        HttpContext httpContext = CreateHttpContext(OrganizationId.ToString("D"));
        AuthorizationHandlerContext context = CreateContext(
            CreatePrincipal(identifiers),
            httpContext);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Equal(0, lookup.CallCount);
    }

    [Theory]
    [MemberData(nameof(InvalidOrganizationContexts))]
    public async Task HandleAsync_InvalidOrganizationContext_DoesNotAuthorizeOrInvokeLiveAuthorization(
        bool includeRouteValue,
        object? routeValue)
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Member);
        var handler = CreateHandler(lookup);
        var httpContext = new DefaultHttpContext();

        if (includeRouteValue)
        {
            httpContext.Request.RouteValues[
                EnmaAuthorizationPolicies.OrganizationIdRouteValue] = routeValue;
        }

        AuthorizationHandlerContext context = CreateContext(
            CreatePrincipal(UserId.ToString("D")),
            httpContext);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Equal(0, lookup.CallCount);
    }

    [Fact]
    public async Task HandleAsync_DeniedLiveAccess_DoesNotSucceedOrModifyPrincipal()
    {
        var lookup = new StubOrganizationAccessLookup(null);
        var handler = CreateHandler(lookup);
        ClaimsPrincipal principal = CreatePrincipal(UserId.ToString("D"));
        Claim[] originalClaims = principal.Claims.ToArray();
        HttpContext httpContext = CreateHttpContext(OrganizationId.ToString("D"));
        AuthorizationHandlerContext context = CreateContext(principal, httpContext);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.False(context.HasFailed);
        Assert.Equal(1, lookup.CallCount);
        Assert.Equal(originalClaims, principal.Claims.ToArray());
    }

    [Fact]
    public async Task HandleAsync_AllowedLiveAccess_SucceedsOnceWithoutPersistingRole()
    {
        using var cancellationSource = new CancellationTokenSource();
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        var handler = CreateHandler(lookup);
        ClaimsPrincipal principal = CreatePrincipal(UserId.ToString("D"));
        HttpContext httpContext = CreateHttpContext(OrganizationId.ToString("D"));
        httpContext.RequestAborted = cancellationSource.Token;
        AuthorizationHandlerContext context = CreateContext(principal, httpContext);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
        Assert.Equal(1, lookup.CallCount);
        Assert.Equal(UserId, lookup.UserId);
        Assert.Equal(OrganizationId, lookup.OrganizationId);
        Assert.Equal(cancellationSource.Token, lookup.CancellationToken);
        Claim claim = Assert.Single(principal.Claims);
        Assert.Equal(ClaimTypes.NameIdentifier, claim.Type);
        Assert.Equal(UserId.ToString("D"), claim.Value);
        Assert.DoesNotContain(principal.Claims, candidate => candidate.Type == ClaimTypes.Role);
    }

    private static OrganizationAccessAuthorizationHandler CreateHandler(
        StubOrganizationAccessLookup lookup)
    {
        return new OrganizationAccessAuthorizationHandler(
            new OrganizationAccessAuthorization(lookup));
    }

    private static AuthorizationHandlerContext CreateContext(
        ClaimsPrincipal principal,
        object resource)
    {
        return new AuthorizationHandlerContext(
            [new OrganizationAccessRequirement()],
            principal,
            resource);
    }

    private static ClaimsPrincipal CreatePrincipal(params string[] identifiers)
    {
        Claim[] claims = identifiers
            .Select(identifier => new Claim(ClaimTypes.NameIdentifier, identifier))
            .ToArray();
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static HttpContext CreateHttpContext(object routeValue)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues[
            EnmaAuthorizationPolicies.OrganizationIdRouteValue] = routeValue;
        return httpContext;
    }

    private sealed class StubOrganizationAccessLookup(
        OrganizationRole? role) : IOrganizationAccessLookup
    {
        public int CallCount { get; private set; }

        public Guid? UserId { get; private set; }

        public Guid? OrganizationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            UserId = userId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;
            return Task.FromResult(role);
        }
    }
}
