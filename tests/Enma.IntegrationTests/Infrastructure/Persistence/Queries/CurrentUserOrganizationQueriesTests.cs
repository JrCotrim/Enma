using Enma.Application.Organizations.CurrentUser;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Queries;

[Collection(PostgreSqlCollection.Name)]
public sealed class CurrentUserOrganizationQueriesTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        12,
        12,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ListAccessibleAsync_WithMultipleUsers_ReturnsOnlyCurrentUsersActiveOrganizationsInDeterministicOrder()
    {
        User currentUser = CreateUser("Current User", "current@example.test");
        User otherUser = CreateUser("Other User", "other@example.test");
        Organization zeta = CreateOrganization("Zeta Legal", "zeta-legal");
        Organization alpha = CreateOrganization("Alpha Legal", "alpha-legal");
        Organization other = CreateOrganization("Other Legal", "other-legal");
        OrganizationMembership zetaMembership = CreateMembership(
            currentUser,
            zeta,
            OrganizationRole.Member);
        OrganizationMembership alphaMembership = CreateMembership(
            currentUser,
            alpha,
            OrganizationRole.Owner);
        OrganizationMembership otherMembership = CreateMembership(
            otherUser,
            other,
            OrganizationRole.Administrator);
        await SeedAsync(
            currentUser,
            otherUser,
            zeta,
            alpha,
            other,
            zetaMembership,
            alphaMembership,
            otherMembership);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new CurrentUserOrganizationQueries(dbContext);

        IReadOnlyList<CurrentUserOrganizationReadModel> result =
            await queries.ListAccessibleAsync(currentUser.Id);

        Assert.Collection(
            result,
            item => AssertOrganization(
                item,
                alpha,
                OrganizationRole.Owner),
            item => AssertOrganization(
                item,
                zeta,
                OrganizationRole.Member));
    }

    [Fact]
    public async Task ListAccessibleAsync_WithInactiveMembershipOrOrganization_ExcludesBoth()
    {
        User user = CreateUser("Current User", "current@example.test");
        Organization active = CreateOrganization("Active Legal", "active-legal");
        Organization inactiveMembershipOrganization = CreateOrganization(
            "Inactive Membership Legal",
            "inactive-membership-legal");
        Organization inactiveOrganization = CreateOrganization(
            "Inactive Organization Legal",
            "inactive-organization-legal");
        inactiveOrganization.Deactivate();
        OrganizationMembership activeMembership = CreateMembership(
            user,
            active,
            OrganizationRole.Administrator);
        OrganizationMembership inactiveMembership = CreateMembership(
            user,
            inactiveMembershipOrganization,
            OrganizationRole.Owner);
        inactiveMembership.Deactivate();
        OrganizationMembership inactiveOrganizationMembership = CreateMembership(
            user,
            inactiveOrganization,
            OrganizationRole.Member);
        await SeedAsync(
            user,
            active,
            inactiveMembershipOrganization,
            inactiveOrganization,
            activeMembership,
            inactiveMembership,
            inactiveOrganizationMembership);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new CurrentUserOrganizationQueries(dbContext);

        IReadOnlyList<CurrentUserOrganizationReadModel> result =
            await queries.ListAccessibleAsync(user.Id);

        CurrentUserOrganizationReadModel item = Assert.Single(result);
        AssertOrganization(item, active, OrganizationRole.Administrator);
    }

    [Fact]
    public async Task ListAccessibleAsync_AfterRoleChangeAndMembershipDeactivation_ReflectsLiveState()
    {
        User user = CreateUser("Current User", "current@example.test");
        Organization organization = CreateOrganization(
            "Current Legal",
            "current-legal");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Member);
        await SeedAsync(user, organization, membership);

        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        var queries = new CurrentUserOrganizationQueries(queryContext);

        CurrentUserOrganizationReadModel initial = Assert.Single(
            await queries.ListAccessibleAsync(user.Id));

        await using (EnmaDbContext mutationContext = fixture.CreateDbContext())
        {
            OrganizationMembership persistedMembership =
                await mutationContext.OrganizationMemberships.SingleAsync();
            persistedMembership.ChangeRole(OrganizationRole.Administrator);
            await mutationContext.SaveChangesAsync();
        }

        CurrentUserOrganizationReadModel changed = Assert.Single(
            await queries.ListAccessibleAsync(user.Id));

        await using (EnmaDbContext mutationContext = fixture.CreateDbContext())
        {
            OrganizationMembership persistedMembership =
                await mutationContext.OrganizationMemberships.SingleAsync();
            persistedMembership.Deactivate();
            await mutationContext.SaveChangesAsync();
        }

        IReadOnlyList<CurrentUserOrganizationReadModel> deactivated =
            await queries.ListAccessibleAsync(user.Id);

        Assert.Equal(OrganizationRole.Member, initial.Role);
        Assert.Equal(OrganizationRole.Administrator, changed.Role);
        Assert.Empty(deactivated);
    }

    [Fact]
    public async Task ListAccessibleAsync_AfterOrganizationStateChanges_ReflectsLiveState()
    {
        User user = CreateUser("Current User", "current@example.test");
        Organization organization = CreateOrganization(
            "Current Legal",
            "current-legal");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        await SeedAsync(user, organization, membership);

        await using EnmaDbContext queryContext = fixture.CreateDbContext();
        var queries = new CurrentUserOrganizationQueries(queryContext);
        Assert.Single(await queries.ListAccessibleAsync(user.Id));

        await using (EnmaDbContext mutationContext = fixture.CreateDbContext())
        {
            Organization persistedOrganization =
                await mutationContext.Organizations.SingleAsync();
            persistedOrganization.Deactivate();
            await mutationContext.SaveChangesAsync();
        }

        Assert.Empty(await queries.ListAccessibleAsync(user.Id));

        await using (EnmaDbContext mutationContext = fixture.CreateDbContext())
        {
            Organization persistedOrganization =
                await mutationContext.Organizations.SingleAsync();
            persistedOrganization.Activate();
            await mutationContext.SaveChangesAsync();
        }

        Assert.Single(await queries.ListAccessibleAsync(user.Id));
    }

    [Fact]
    public async Task ListAccessibleAsync_WithNoMemberships_ReturnsEmptyCollection()
    {
        User user = CreateUser("Current User", "current@example.test");
        await SeedAsync(user);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new CurrentUserOrganizationQueries(dbContext);

        IReadOnlyList<CurrentUserOrganizationReadModel> result =
            await queries.ListAccessibleAsync(user.Id);

        Assert.Empty(result);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static User CreateUser(string name, string email)
    {
        return new User(name, email, CreatedAt);
    }

    private static Organization CreateOrganization(string name, string slug)
    {
        return new Organization(name, slug, CreatedAt);
    }

    private static OrganizationMembership CreateMembership(
        User user,
        Organization organization,
        OrganizationRole role)
    {
        return new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            CreatedAt);
    }

    private static void AssertOrganization(
        CurrentUserOrganizationReadModel item,
        Organization organization,
        OrganizationRole role)
    {
        Assert.Equal(organization.Id, item.OrganizationId);
        Assert.Equal(organization.Name, item.Name);
        Assert.Equal(role, item.Role);
    }
}
