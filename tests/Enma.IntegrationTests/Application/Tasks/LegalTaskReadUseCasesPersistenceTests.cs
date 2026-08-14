using System.Data.Common;
using Enma.Application.Authorization;
using Enma.Application.Tasks;
using Enma.Application.Tasks.GetById;
using Enma.Application.Tasks.List;
using Enma.Domain.Organizations;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Application.Tasks;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalTaskReadUseCasesPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        14,
        21,
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

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_WithViewRole_AllowsGetAndList(
        OrganizationRole role)
    {
        AccessGraph graph = CreateGraph("Alpha", "alpha", role);
        var legalTask = new LegalTask(
            graph.Organization.Id,
            "Visible task",
            null,
            null,
            null,
            null,
            graph.Membership.Id,
            CreatedAt);
        await SeedAsync(graph.Entities.Append(legalTask).ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        (GetLegalTaskUseCase getUseCase, ListLegalTasksUseCase listUseCase) =
            CreateUseCases(dbContext);

        GetLegalTaskResult get = await getUseCase.ExecuteAsync(
            new GetLegalTaskQuery(
                graph.User.Id,
                graph.Organization.Id,
                legalTask.Id));
        ListLegalTasksResult list = await listUseCase.ExecuteAsync(
            new ListLegalTasksQuery(
                graph.User.Id,
                graph.Organization.Id));

        Assert.Equal(GetLegalTaskResultStatus.Succeeded, get.Status);
        Assert.Equal(legalTask.Id, get.LegalTask?.Id);
        Assert.Equal(ListLegalTasksResultStatus.Succeeded, list.Status);
        Assert.Equal([legalTask.Id], list.Items.Select(item => item.Id));
    }

    [Theory]
    [InlineData(AccessDenial.Missing)]
    [InlineData(AccessDenial.InactiveMembership)]
    [InlineData(AccessDenial.InactiveOrganization)]
    public async Task ExecuteAsync_WithoutLiveOrganizationAccess_DeniesBeforeTaskQuery(
        AccessDenial denial)
    {
        AccessGraph graph = CreateGraph(
            "Alpha",
            "alpha",
            OrganizationRole.Owner);
        if (denial == AccessDenial.InactiveMembership)
        {
            graph.Membership.Deactivate();
        }
        else if (denial == AccessDenial.InactiveOrganization)
        {
            graph.Organization.Deactivate();
        }

        var entities = new List<object>
        {
            graph.Organization,
            graph.User
        };
        if (denial != AccessDenial.Missing)
        {
            entities.Add(graph.Membership);
        }

        await SeedAsync(entities.ToArray());
        var interceptor = new ReaderCommandInterceptor();
        await using EnmaDbContext dbContext = CreateInterceptedContext(interceptor);
        (GetLegalTaskUseCase getUseCase, ListLegalTasksUseCase listUseCase) =
            CreateUseCases(dbContext);

        GetLegalTaskResult get = await getUseCase.ExecuteAsync(
            new GetLegalTaskQuery(
                graph.User.Id,
                graph.Organization.Id,
                Guid.NewGuid()));
        ListLegalTasksResult list = await listUseCase.ExecuteAsync(
            new ListLegalTasksQuery(
                graph.User.Id,
                graph.Organization.Id));

        Assert.Same(GetLegalTaskResult.AccessDenied, get);
        Assert.Same(ListLegalTasksResult.AccessDenied, list);
        Assert.Equal(2, interceptor.ReaderCommandCount);
        Assert.All(
            interceptor.CommandTexts,
            commandText => Assert.DoesNotContain("legal_tasks", commandText));
    }

    [Fact]
    public async Task ExecuteAsync_DualMembershipSelf_UsesContextualMembershipAndTenant()
    {
        var organizationA = new Organization("Alpha", "alpha", CreatedAt);
        var organizationB = new Organization("Beta", "beta", CreatedAt);
        var user = new User("Dual user", "dual@example.test", CreatedAt);
        var membershipA = new OrganizationMembership(
            organizationA.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt);
        var membershipB = new OrganizationMembership(
            organizationB.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt);
        var taskA = new LegalTask(
            organizationA.Id,
            "Alpha self",
            null,
            null,
            null,
            membershipA.Id,
            membershipA.Id,
            CreatedAt);
        var taskB = new LegalTask(
            organizationB.Id,
            "Beta self",
            null,
            null,
            null,
            membershipB.Id,
            membershipB.Id,
            CreatedAt);
        await SeedAsync(
            organizationA,
            organizationB,
            user,
            membershipA,
            membershipB,
            taskA,
            taskB);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        (_, ListLegalTasksUseCase listUseCase) = CreateUseCases(dbContext);

        ListLegalTasksResult listA = await listUseCase.ExecuteAsync(
            new ListLegalTasksQuery(
                user.Id,
                organizationA.Id,
                Assignee: LegalTaskAssigneeFilter.Self));
        ListLegalTasksResult listB = await listUseCase.ExecuteAsync(
            new ListLegalTasksQuery(
                user.Id,
                organizationB.Id,
                Assignee: LegalTaskAssigneeFilter.Self));

        Assert.Equal([taskA.Id], listA.Items.Select(item => item.Id));
        Assert.Equal([taskB.Id], listB.Items.Select(item => item.Id));
        Assert.Equal(membershipA.Id, listA.Items.Single().AssigneeMembershipId);
        Assert.Equal(membershipB.Id, listB.Items.Single().AssigneeMembershipId);
        Assert.NotEqual(user.Id, listA.Items.Single().AssigneeMembershipId);
        Assert.NotEqual(user.Id, listB.Items.Single().AssigneeMembershipId);
    }

    [Fact]
    public async Task ExecuteAsync_MissingAndCrossTenantGet_ReturnSameNotFound()
    {
        AccessGraph graphA = CreateGraph(
            "Alpha",
            "alpha",
            OrganizationRole.Owner);
        AccessGraph graphB = CreateGraph(
            "Beta",
            "beta",
            OrganizationRole.Owner);
        var taskB = new LegalTask(
            graphB.Organization.Id,
            "Beta task",
            null,
            null,
            null,
            null,
            graphB.Membership.Id,
            CreatedAt);
        await SeedAsync(
            graphA.Entities
                .Concat(graphB.Entities)
                .Append(taskB)
                .ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        (GetLegalTaskUseCase getUseCase, _) = CreateUseCases(dbContext);

        GetLegalTaskResult missing = await getUseCase.ExecuteAsync(
            new GetLegalTaskQuery(
                graphA.User.Id,
                graphA.Organization.Id,
                Guid.NewGuid()));
        GetLegalTaskResult crossTenant = await getUseCase.ExecuteAsync(
            new GetLegalTaskQuery(
                graphA.User.Id,
                graphA.Organization.Id,
                taskB.Id));

        Assert.Same(GetLegalTaskResult.NotFound, missing);
        Assert.Same(missing, crossTenant);
    }

    private static (GetLegalTaskUseCase Get, ListLegalTasksUseCase List)
        CreateUseCases(EnmaDbContext dbContext)
    {
        var viewAuthorization = new LegalTaskViewAuthorization(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(dbContext)));
        var queries = new LegalTaskReadQueries(dbContext);
        return (
            new GetLegalTaskUseCase(viewAuthorization, queries),
            new ListLegalTasksUseCase(viewAuthorization, queries));
    }

    private EnmaDbContext CreateInterceptedContext(
        DbCommandInterceptor interceptor)
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(interceptor)
                .Options;
        return new EnmaDbContext(options);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static AccessGraph CreateGraph(
        string name,
        string slug,
        OrganizationRole role)
    {
        var organization = new Organization(name, slug, CreatedAt);
        var user = new User(
            $"{name} user",
            $"{slug}@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            CreatedAt);
        return new AccessGraph(organization, user, membership);
    }

    public enum AccessDenial
    {
        Missing = 0,
        InactiveMembership = 1,
        InactiveOrganization = 2
    }

    private sealed record AccessGraph(
        Organization Organization,
        User User,
        OrganizationMembership Membership)
    {
        public object[] Entities => [Organization, User, Membership];
    }

    private sealed class ReaderCommandInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commandTexts = [];

        public int ReaderCommandCount => _commandTexts.Count;

        public IReadOnlyList<string> CommandTexts => _commandTexts;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commandTexts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
