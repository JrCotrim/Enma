using System.Data.Common;
using Enma.Application.Organizations.UpdateName;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationNameMutationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        26,
        12,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ExecuteAsync_OwnerRename_PersistsOnlyName()
    {
        TestGraph graph = await SeedGraphAsync();
        OrganizationSnapshot before = await FindOrganizationAsync(
            graph.Organization.Id);

        OrganizationNameMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(
                CreateRequest(graph, "Renamed Legal"));

        OrganizationSnapshot after = await FindOrganizationAsync(
            graph.Organization.Id);
        Assert.Equal(OrganizationNameMutationPersistenceResult.Succeeded, result);
        Assert.Equal("Renamed Legal", after.Name);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Slug, after.Slug);
        Assert.Equal(before.IsActive, after.IsActive);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
    }

    [Fact]
    public async Task ExecuteAsync_WhitespaceName_UsesDomainNormalization()
    {
        TestGraph graph = await SeedGraphAsync();

        OrganizationNameMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(
                CreateRequest(graph, "  Normalized Legal  "));

        Assert.Equal(OrganizationNameMutationPersistenceResult.Succeeded, result);
        Assert.Equal(
            "Normalized Legal",
            (await FindOrganizationAsync(graph.Organization.Id)).Name);
    }

    [Fact]
    public async Task ExecuteAsync_SameNormalizedName_IsIdempotent()
    {
        TestGraph graph = await SeedGraphAsync();
        var interceptor = new CommandRecordingInterceptor();

        OrganizationNameMutationPersistenceResult result =
            await CreatePersistence(interceptor).ExecuteAsync(
                CreateRequest(graph, $"  {graph.Organization.Name}  "));

        Assert.Equal(OrganizationNameMutationPersistenceResult.Succeeded, result);
        Assert.Equal(
            graph.Organization.Name,
            (await FindOrganizationAsync(graph.Organization.Id)).Name);
        Assert.DoesNotContain(
            interceptor.Commands,
            command => command.StartsWith(
                "UPDATE organizations",
                StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(ActorState.Administrator)]
    [InlineData(ActorState.Member)]
    [InlineData(ActorState.InactiveMembership)]
    [InlineData(ActorState.InactiveUser)]
    [InlineData(ActorState.InactiveOrganization)]
    public async Task ExecuteAsync_UnavailableLiveOwner_DeniesWithoutRename(
        ActorState actorState)
    {
        TestGraph graph = await SeedGraphAsync(
            actorRole: actorState switch
            {
                ActorState.Administrator => OrganizationRole.Administrator,
                ActorState.Member => OrganizationRole.Member,
                _ => OrganizationRole.Owner
            },
            actorMembershipActive: actorState != ActorState.InactiveMembership,
            actorUserActive: actorState != ActorState.InactiveUser,
            organizationActive: actorState != ActorState.InactiveOrganization);

        OrganizationNameMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(
                CreateRequest(graph, "Denied Legal"));

        Assert.Equal(
            OrganizationNameMutationPersistenceResult.AccessDenied,
            result);
        Assert.Equal(
            graph.Organization.Name,
            (await FindOrganizationAsync(graph.Organization.Id)).Name);
    }

    [Fact]
    public async Task ExecuteAsync_ActorMembershipFromAnotherOrganization_Denies()
    {
        TestGraph graph = await SeedGraphAsync();
        Organization foreignOrganization = CreateOrganization("Foreign");
        User foreignUser = CreateUser("Foreign Owner");
        var foreignMembership = new OrganizationMembership(
            foreignOrganization.Id,
            foreignUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        await SeedAsync(foreignOrganization, foreignUser, foreignMembership);

        OrganizationNameMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(
                CreateRequest(graph, "Denied Legal") with
                {
                    ActorMembershipId = foreignMembership.Id
                });

        Assert.Equal(
            OrganizationNameMutationPersistenceResult.AccessDenied,
            result);
        Assert.Equal(
            graph.Organization.Name,
            (await FindOrganizationAsync(graph.Organization.Id)).Name);
    }

    [Fact]
    public async Task ExecuteAsync_ActorMembershipUserMismatch_Denies()
    {
        TestGraph graph = await SeedGraphAsync();
        User otherUser = CreateUser("Other User");
        await SeedAsync(otherUser);

        OrganizationNameMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(
                CreateRequest(graph, "Denied Legal") with
                {
                    UserId = otherUser.Id
                });

        Assert.Equal(
            OrganizationNameMutationPersistenceResult.AccessDenied,
            result);
        Assert.Equal(
            graph.Organization.Name,
            (await FindOrganizationAsync(graph.Organization.Id)).Name);
    }

    [Fact]
    public async Task ExecuteAsync_RenamesOnlyRequestedOrganization()
    {
        TestGraph graph = await SeedGraphAsync();
        Organization otherOrganization = CreateOrganization("Similar");
        await SeedAsync(otherOrganization);

        OrganizationNameMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(
                CreateRequest(graph, otherOrganization.Name));

        Assert.Equal(OrganizationNameMutationPersistenceResult.Succeeded, result);
        Assert.Equal(
            otherOrganization.Name,
            (await FindOrganizationAsync(graph.Organization.Id)).Name);
        OrganizationSnapshot unchangedOther = await FindOrganizationAsync(
            otherOrganization.Id);
        Assert.Equal(otherOrganization.Name, unchangedOther.Name);
        Assert.Equal(otherOrganization.Slug, unchangedOther.Slug);
    }

    [Fact]
    public async Task ExecuteAsync_LocksOnlyRequiredRowsInCanonicalOrder()
    {
        TestGraph graph = await SeedGraphAsync();
        var interceptor = new CommandRecordingInterceptor();

        OrganizationNameMutationPersistenceResult result =
            await CreatePersistence(interceptor).ExecuteAsync(
                CreateRequest(graph, "Locked Legal"));

        Assert.Equal(OrganizationNameMutationPersistenceResult.Succeeded, result);
        CommandSnapshot organizationLock = Assert.Single(
            interceptor.ReaderCommands,
            command => command.Text.Contains(
                "FROM organizations",
                StringComparison.Ordinal));
        CommandSnapshot membershipLock = Assert.Single(
            interceptor.ReaderCommands,
            command => command.Text.Contains(
                "FROM organization_memberships",
                StringComparison.Ordinal));
        CommandSnapshot userLock = Assert.Single(
            interceptor.ReaderCommands,
            command => command.Text.Contains("FROM users", StringComparison.Ordinal));
        Assert.True(
            interceptor.ReaderCommands.IndexOf(organizationLock) <
            interceptor.ReaderCommands.IndexOf(membershipLock));
        Assert.True(
            interceptor.ReaderCommands.IndexOf(membershipLock) <
            interceptor.ReaderCommands.IndexOf(userLock));
        Assert.Contains("FOR UPDATE", organizationLock.Text);
        Assert.Contains(graph.Organization.Id, organizationLock.ParameterValues);
        Assert.Contains("organization_id", membershipLock.Text);
        Assert.Contains("FOR UPDATE", membershipLock.Text);
        Assert.Contains(graph.Organization.Id, membershipLock.ParameterValues);
        Assert.Contains(graph.ActorMembership.Id, membershipLock.ParameterValues);
        Assert.Contains("FOR UPDATE", userLock.Text);
        Assert.Contains(graph.ActorUser.Id, userLock.ParameterValues);
    }

    [Theory]
    [InlineData(InvalidContext.User)]
    [InlineData(InvalidContext.Organization)]
    [InlineData(InvalidContext.Membership)]
    public async Task ExecuteAsync_EmptyAuthoritativeIdentifier_FailsClosed(
        InvalidContext invalidContext)
    {
        TestGraph graph = await SeedGraphAsync();
        OrganizationNameMutationPersistenceRequest request =
            CreateRequest(graph, "Denied Legal") with
            {
                UserId = invalidContext == InvalidContext.User
                    ? Guid.Empty
                    : graph.ActorUser.Id,
                OrganizationId = invalidContext == InvalidContext.Organization
                    ? Guid.Empty
                    : graph.Organization.Id,
                ActorMembershipId = invalidContext == InvalidContext.Membership
                    ? Guid.Empty
                    : graph.ActorMembership.Id
            };

        OrganizationNameMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(request);

        Assert.Equal(
            OrganizationNameMutationPersistenceResult.InvalidInput,
            result);
        Assert.Equal(
            graph.Organization.Name,
            (await FindOrganizationAsync(graph.Organization.Id)).Name);
    }

    private OrganizationNameMutationPersistence CreatePersistence(
        DbCommandInterceptor? interceptor = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString);

        if (interceptor is not null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        return new OrganizationNameMutationPersistence(optionsBuilder.Options);
    }

    private static OrganizationNameMutationPersistenceRequest CreateRequest(
        TestGraph graph,
        string name)
    {
        return new OrganizationNameMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            name);
    }

    private async Task<TestGraph> SeedGraphAsync(
        OrganizationRole actorRole = OrganizationRole.Owner,
        bool actorMembershipActive = true,
        bool actorUserActive = true,
        bool organizationActive = true)
    {
        Organization organization = CreateOrganization("Current");
        User actorUser = CreateUser("Owner Actor");
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actorUser.Id,
            actorRole,
            CreatedAt);

        if (!organizationActive)
        {
            organization.Deactivate();
        }

        if (!actorUserActive)
        {
            actorUser.Deactivate();
        }

        if (!actorMembershipActive)
        {
            actorMembership.Deactivate();
        }

        await SeedAsync(organization, actorUser, actorMembership);
        return new TestGraph(organization, actorUser, actorMembership);
    }

    private async Task<OrganizationSnapshot> FindOrganizationAsync(
        Guid organizationId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == organizationId)
            .Select(organization => new OrganizationSnapshot(
                organization.Id,
                organization.Name,
                organization.Slug,
                organization.IsActive,
                organization.CreatedAt))
            .SingleAsync();
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static Organization CreateOrganization(string marker)
    {
        return new Organization(
            $"{marker} Legal",
            $"{marker.ToLowerInvariant()}-{Guid.NewGuid():N}",
            CreatedAt);
    }

    private static User CreateUser(string marker)
    {
        return new User(
            marker,
            $"{marker.ToLowerInvariant().Replace(' ', '.')}+{Guid.NewGuid():N}@example.test",
            CreatedAt);
    }

    public enum ActorState
    {
        Administrator = 0,
        Member = 1,
        InactiveMembership = 2,
        InactiveUser = 3,
        InactiveOrganization = 4
    }

    public enum InvalidContext
    {
        User = 0,
        Organization = 1,
        Membership = 2
    }

    private sealed record TestGraph(
        Organization Organization,
        User ActorUser,
        OrganizationMembership ActorMembership);

    private sealed record OrganizationSnapshot(
        Guid Id,
        string Name,
        string Slug,
        bool IsActive,
        DateTimeOffset CreatedAt);

    private sealed class CommandRecordingInterceptor : DbCommandInterceptor
    {
        public List<CommandSnapshot> ReaderCommands { get; } = [];

        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            ReaderCommands.Add(new CommandSnapshot(
                command.CommandText,
                command.Parameters
                    .Cast<DbParameter>()
                    .Select(parameter => parameter.Value)
                    .ToArray()));
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed record CommandSnapshot(
        string Text,
        IReadOnlyList<object?> ParameterValues);
}
