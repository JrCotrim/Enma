using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Authorization;

public sealed class CalendarEventAccessAuthorizationTests
{
    private static readonly Guid UserId = Guid.Parse(
        "0234abbf-622f-4022-817a-3c9ba95ea3e8");
    private static readonly Guid OrganizationId = Guid.Parse(
        "e24fd4bc-5f7f-4ea7-a934-d094a594ad27");
    private static readonly Guid MembershipId = Guid.Parse(
        "9acb20f4-2bc2-4781-89e4-91f03862accf");

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task AuthorizeAsync_WithLiveAccess_ReturnsAuthoritativeIdentity(
        OrganizationRole role)
    {
        var authorization = CreateAuthorization(
            new OrganizationAccessLookupResult(
                UserId,
                OrganizationId,
                MembershipId,
                role));

        CalendarEventAccessAuthorizationResult result =
            await authorization.AuthorizeAsync(UserId, OrganizationId);

        Assert.Equal(CalendarEventAccessAuthorizationStatus.Allowed, result.Status);
        Assert.Equal(UserId, result.UserId);
        Assert.Equal(OrganizationId, result.OrganizationId);
        Assert.Equal(MembershipId, result.MembershipId);
        Assert.Equal(role, result.Role);
    }

    [Fact]
    public async Task AuthorizeAsync_WithoutLiveAccess_Denies()
    {
        CalendarEventAccessAuthorization authorization =
            CreateAuthorization(null);

        CalendarEventAccessAuthorizationResult result =
            await authorization.AuthorizeAsync(UserId, OrganizationId);

        Assert.Same(CalendarEventAccessAuthorizationResult.Denied, result);
    }

    [Fact]
    public async Task AuthorizeAsync_WithMismatchedOrUndefinedState_Denies()
    {
        CalendarEventAccessAuthorization mismatched = CreateAuthorization(
            new OrganizationAccessLookupResult(
                Guid.NewGuid(),
                OrganizationId,
                MembershipId,
                OrganizationRole.Owner));
        CalendarEventAccessAuthorization undefined = CreateAuthorization(
            new OrganizationAccessLookupResult(
                UserId,
                OrganizationId,
                MembershipId,
                (OrganizationRole)int.MaxValue));

        Assert.Same(
            CalendarEventAccessAuthorizationResult.Denied,
            await mismatched.AuthorizeAsync(UserId, OrganizationId));
        Assert.Same(
            CalendarEventAccessAuthorizationResult.Denied,
            await undefined.AuthorizeAsync(UserId, OrganizationId));
    }

    private static CalendarEventAccessAuthorization CreateAuthorization(
        OrganizationAccessLookupResult? result)
    {
        return new CalendarEventAccessAuthorization(
            new OrganizationAccessAuthorization(new StubAccessLookup(result)));
    }

    private sealed class StubAccessLookup(OrganizationAccessLookupResult? result)
        : IOrganizationAccessLookup
    {
        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException();
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(result);
        }
    }
}
