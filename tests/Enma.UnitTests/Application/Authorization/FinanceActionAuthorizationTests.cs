using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Authorization;

public sealed class FinanceActionAuthorizationTests
{
    private static readonly Guid UserId = Guid.Parse(
        "6828998b-c724-42f5-b0fb-8288995e4a8b");

    private static readonly Guid OrganizationId = Guid.Parse(
        "a7cb90bb-2c89-49e0-9a42-34834a1ba7af");

    [Theory]
    [InlineData(FinanceAction.View, OrganizationRole.Owner, true)]
    [InlineData(FinanceAction.CreatePaymentPlan, OrganizationRole.Owner, true)]
    [InlineData(FinanceAction.MarkInstallmentPaid, OrganizationRole.Owner, true)]
    [InlineData(FinanceAction.View, OrganizationRole.Administrator, true)]
    [InlineData(FinanceAction.CreatePaymentPlan, OrganizationRole.Administrator, true)]
    [InlineData(FinanceAction.MarkInstallmentPaid, OrganizationRole.Administrator, true)]
    [InlineData(FinanceAction.View, OrganizationRole.Member, false)]
    [InlineData(FinanceAction.CreatePaymentPlan, OrganizationRole.Member, false)]
    [InlineData(FinanceAction.MarkInstallmentPaid, OrganizationRole.Member, false)]
    public async Task AuthorizeAsync_WithLiveRole_AppliesExplicitActionRule(
        FinanceAction action,
        OrganizationRole role,
        bool expectedAllowed)
    {
        var lookup = new StubOrganizationAccessLookup(role);
        FinanceActionAuthorization authorization = CreateAuthorization(lookup);

        FinanceActionAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            action);

        Assert.Equal(
            expectedAllowed
                ? FinanceActionAuthorizationResult.Allowed
                : FinanceActionAuthorizationResult.Denied,
            result);
    }

    [Fact]
    public async Task AuthorizeAsync_WithDeniedOrganizationAccess_ReturnsDenied()
    {
        FinanceActionAuthorization authorization = CreateAuthorization(
            new StubOrganizationAccessLookup(null));

        FinanceActionAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            FinanceAction.View);

        Assert.Equal(FinanceActionAuthorizationResult.Denied, result);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AuthorizeAsync_WithEmptyContext_DeniesWithoutLookup(
        bool emptyUserId,
        bool emptyOrganizationId)
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        FinanceActionAuthorization authorization = CreateAuthorization(lookup);

        FinanceActionAuthorizationResult result = await authorization.AuthorizeAsync(
            emptyUserId ? Guid.Empty : UserId,
            emptyOrganizationId ? Guid.Empty : OrganizationId,
            FinanceAction.View);

        Assert.Equal(FinanceActionAuthorizationResult.Denied, result);
        Assert.Equal(0, lookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithUndefinedAction_DeniesWithoutLookup()
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        FinanceActionAuthorization authorization = CreateAuthorization(lookup);

        FinanceActionAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            (FinanceAction)int.MaxValue);

        Assert.Equal(FinanceActionAuthorizationResult.Denied, result);
        Assert.Equal(0, lookup.CallCount);
    }

    [Fact]
    public async Task AuthorizeAsync_WithUndefinedLiveRole_ReturnsDenied()
    {
        FinanceActionAuthorization authorization = CreateAuthorization(
            new StubOrganizationAccessLookup((OrganizationRole)int.MaxValue));

        FinanceActionAuthorizationResult result = await authorization.AuthorizeAsync(
            UserId,
            OrganizationId,
            FinanceAction.View);

        Assert.Equal(FinanceActionAuthorizationResult.Denied, result);
    }

    private static FinanceActionAuthorization CreateAuthorization(
        StubOrganizationAccessLookup lookup)
    {
        return new FinanceActionAuthorization(
            new OrganizationAccessAuthorization(lookup));
    }

    private sealed class StubOrganizationAccessLookup(OrganizationRole? role)
        : IOrganizationAccessLookup
    {
        public int CallCount { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(role);
        }
    }
}
