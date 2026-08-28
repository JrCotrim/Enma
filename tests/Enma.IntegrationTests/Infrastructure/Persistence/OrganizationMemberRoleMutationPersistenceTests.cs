using System.Data.Common;
using Enma.Application.Organizations.Members.Role;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationMemberRoleMutationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        25,
        12,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateTimeOffset OccurredAt = CreatedAt.AddHours(3);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(OrganizationRole.Member, OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Administrator, OrganizationRole.Member)]
    public async Task ExecuteAsync_SupportedOppositeTransition_Persists(
        OrganizationRole currentRole,
        OrganizationRole requestedRole)
    {
        TestGraph graph = await SeedGraphAsync(targetRole: currentRole);
        OrganizationMemberRoleMutationPersistence persistence = CreatePersistence();

        OrganizationMemberRoleMutationPersistenceResult result =
            await persistence.ExecuteAsync(CreateRequest(
                graph,
                requestedRole,
                currentRole));

        Assert.Equal(
            OrganizationMemberRoleMutationPersistenceResult.Succeeded,
            result);
        Assert.Equal(requestedRole, await FindRoleAsync(graph.TargetMembership.Id));
        AuditLog auditLog = await FindSingleAuditLogAsync();
        Assert.Equal(graph.Organization.Id, auditLog.OrganizationId);
        Assert.Equal(graph.ActorUser.Id, auditLog.ActorUserId);
        Assert.Equal(graph.ActorMembership.Id, auditLog.ActorMembershipId);
        Assert.Equal(OrganizationRole.Owner, auditLog.ActorRoleAtOccurrence);
        Assert.Equal(
            AuditEventType.OrganizationMembershipRoleChanged,
            auditLog.EventType);
        Assert.Equal(AuditEntityType.OrganizationMembership, auditLog.EntityType);
        Assert.Equal(graph.TargetMembership.Id, auditLog.EntityId);
        Assert.Equal(OccurredAt, auditLog.OccurredAt);
        OrganizationMembershipRoleChangedAuditDetails details =
            Assert.IsType<OrganizationMembershipRoleChangedAuditDetails>(
                auditLog.Details);
        Assert.Equal(currentRole, details.OldRole);
        Assert.Equal(requestedRole, details.NewRole);
    }

    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_RequestedRoleAlreadyCurrent_IsIdempotent(
        OrganizationRole currentRole)
    {
        TestGraph graph = await SeedGraphAsync(targetRole: currentRole);
        OrganizationRole staleExpectedRole = currentRole == OrganizationRole.Member
            ? OrganizationRole.Administrator
            : OrganizationRole.Member;

        OrganizationMemberRoleMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(CreateRequest(
                graph,
                currentRole,
                staleExpectedRole));

        Assert.Equal(
            OrganizationMemberRoleMutationPersistenceResult.Succeeded,
            result);
        Assert.Equal(currentRole, await FindRoleAsync(graph.TargetMembership.Id));
        Assert.Equal(0, await CountAuditLogsAsync());
    }

    [Fact]
    public async Task ExecuteAsync_ExpectedRoleMismatch_ReturnsConflictWithoutWrite()
    {
        TestGraph graph = await SeedGraphAsync(
            targetRole: OrganizationRole.Administrator);

        OrganizationMemberRoleMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(CreateRequest(
                graph,
                OrganizationRole.Member,
                OrganizationRole.Member));

        Assert.Equal(
            OrganizationMemberRoleMutationPersistenceResult.Conflict,
            result);
        Assert.Equal(
            OrganizationRole.Administrator,
            await FindRoleAsync(graph.TargetMembership.Id));
        Assert.Equal(0, await CountAuditLogsAsync());
    }

    [Fact]
    public async Task ExecuteAsync_InactiveTarget_ReturnsConflictWithoutWrite()
    {
        TestGraph graph = await SeedGraphAsync(
            targetRole: OrganizationRole.Member,
            targetMembershipActive: false);

        OrganizationMemberRoleMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(CreateRequest(
                graph,
                OrganizationRole.Administrator,
                OrganizationRole.Member));

        Assert.Equal(
            OrganizationMemberRoleMutationPersistenceResult.Conflict,
            result);
        Assert.Equal(
            OrganizationRole.Member,
            await FindRoleAsync(graph.TargetMembership.Id));
    }

    [Fact]
    public async Task ExecuteAsync_OwnerTarget_IsForbiddenAndNeverMutated()
    {
        TestGraph graph = await SeedGraphAsync(targetRole: OrganizationRole.Owner);

        OrganizationMemberRoleMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(CreateRequest(
                graph,
                OrganizationRole.Member,
                OrganizationRole.Administrator));

        Assert.Equal(
            OrganizationMemberRoleMutationPersistenceResult.TargetForbidden,
            result);
        Assert.Equal(
            OrganizationRole.Owner,
            await FindRoleAsync(graph.TargetMembership.Id));
        Assert.Equal(0, await CountAuditLogsAsync());
    }

    [Fact]
    public async Task ExecuteAsync_ForeignTenantMembershipId_ReturnsNotFound()
    {
        TestGraph graph = await SeedGraphAsync(targetRole: OrganizationRole.Member);
        Organization foreignOrganization = CreateOrganization("Foreign");
        User foreignUser = CreateUser("Foreign Target");
        var foreignMembership = new OrganizationMembership(
            foreignOrganization.Id,
            foreignUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        await SeedAsync(foreignOrganization, foreignUser, foreignMembership);

        OrganizationMemberRoleMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(CreateRequest(
                graph,
                OrganizationRole.Administrator,
                OrganizationRole.Member) with
            {
                TargetMembershipId = foreignMembership.Id
            });

        Assert.Equal(
            OrganizationMemberRoleMutationPersistenceResult.NotFound,
            result);
        Assert.Equal(
            OrganizationRole.Member,
            await FindRoleAsync(foreignMembership.Id));
    }

    [Theory]
    [InlineData(ActorState.Administrator)]
    [InlineData(ActorState.Member)]
    [InlineData(ActorState.InactiveMembership)]
    [InlineData(ActorState.InactiveUser)]
    [InlineData(ActorState.InactiveOrganization)]
    public async Task ExecuteAsync_UnavailableLiveActor_DeniesWithoutTargetWrite(
        ActorState actorState)
    {
        TestGraph graph = await SeedGraphAsync(
            targetRole: OrganizationRole.Member,
            actorRole: actorState switch
            {
                ActorState.Administrator => OrganizationRole.Administrator,
                ActorState.Member => OrganizationRole.Member,
                _ => OrganizationRole.Owner
            },
            actorMembershipActive: actorState != ActorState.InactiveMembership,
            actorUserActive: actorState != ActorState.InactiveUser,
            organizationActive: actorState != ActorState.InactiveOrganization);

        OrganizationMemberRoleMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(CreateRequest(
                graph,
                OrganizationRole.Administrator,
                OrganizationRole.Member));

        Assert.Equal(
            OrganizationMemberRoleMutationPersistenceResult.AccessDenied,
            result);
        Assert.Equal(
            OrganizationRole.Member,
            await FindRoleAsync(graph.TargetMembership.Id));
        Assert.Equal(0, await CountAuditLogsAsync());
    }

    [Theory]
    [InlineData(OrganizationRole.Owner, OrganizationRole.Member)]
    [InlineData(OrganizationRole.Member, OrganizationRole.Owner)]
    [InlineData((OrganizationRole)999, OrganizationRole.Member)]
    [InlineData(OrganizationRole.Member, (OrganizationRole)999)]
    public async Task ExecuteAsync_NonMutableRequestRole_FailsClosedWithoutWrite(
        OrganizationRole role,
        OrganizationRole expectedCurrentRole)
    {
        TestGraph graph = await SeedGraphAsync(targetRole: OrganizationRole.Member);

        OrganizationMemberRoleMutationPersistenceResult result =
            await CreatePersistence().ExecuteAsync(CreateRequest(
                graph,
                role,
                expectedCurrentRole));

        Assert.Equal(
            OrganizationMemberRoleMutationPersistenceResult.InvalidInput,
            result);
        Assert.Equal(
            OrganizationRole.Member,
            await FindRoleAsync(graph.TargetMembership.Id));
    }

    [Fact]
    public async Task ExecuteAsync_LocksQualifiedRowsInDeterministicOrder()
    {
        TestGraph graph = await SeedGraphAsync(targetRole: OrganizationRole.Member);
        var interceptor = new CommandRecordingInterceptor();
        OrganizationMemberRoleMutationPersistence persistence = CreatePersistence(
            interceptor);

        OrganizationMemberRoleMutationPersistenceResult result =
            await persistence.ExecuteAsync(CreateRequest(
                graph,
                OrganizationRole.Administrator,
                OrganizationRole.Member));

        Assert.Equal(
            OrganizationMemberRoleMutationPersistenceResult.Succeeded,
            result);
        CommandSnapshot organizationLock = Assert.Single(
            interceptor.Commands,
            command => command.Text.Contains(
                "FROM organizations",
                StringComparison.Ordinal));
        CommandSnapshot membershipLock = Assert.Single(
            interceptor.Commands,
            command => command.Text.Contains(
                "FROM organization_memberships",
                StringComparison.Ordinal));
        CommandSnapshot actorUserLock = Assert.Single(
            interceptor.Commands,
            command => command.Text.Contains("FROM users", StringComparison.Ordinal));
        Assert.Contains("FOR UPDATE", organizationLock.Text);
        Assert.Contains(graph.Organization.Id, organizationLock.ParameterValues);
        Assert.Contains("organization_id", membershipLock.Text);
        Assert.Contains("id = ANY", membershipLock.Text);
        Assert.Contains("ORDER BY id", membershipLock.Text);
        Assert.Contains("FOR UPDATE", membershipLock.Text);
        Assert.Contains(graph.Organization.Id, membershipLock.ParameterValues);
        Guid[] lockedMembershipIds = Assert.IsType<Guid[]>(
            Assert.Single(membershipLock.ParameterValues, value => value is Guid[]));
        Assert.Equal(
            new[] { graph.ActorMembership.Id, graph.TargetMembership.Id }
                .OrderBy(id => id),
            lockedMembershipIds);
        Assert.Contains("FOR UPDATE", actorUserLock.Text);
        Assert.Contains(graph.ActorUser.Id, actorUserLock.ParameterValues);
    }

    private OrganizationMemberRoleMutationPersistence CreatePersistence(
        DbCommandInterceptor? interceptor = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString);

        if (interceptor is not null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        return new OrganizationMemberRoleMutationPersistence(
            optionsBuilder.Options,
            new FixedTimeProvider(OccurredAt));
    }

    private static OrganizationMemberRoleMutationPersistenceRequest CreateRequest(
        TestGraph graph,
        OrganizationRole role,
        OrganizationRole expectedCurrentRole)
    {
        return new OrganizationMemberRoleMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.TargetMembership.Id,
            role,
            expectedCurrentRole);
    }

    private async Task<TestGraph> SeedGraphAsync(
        OrganizationRole targetRole,
        OrganizationRole actorRole = OrganizationRole.Owner,
        bool actorMembershipActive = true,
        bool actorUserActive = true,
        bool organizationActive = true,
        bool targetMembershipActive = true)
    {
        Organization organization = CreateOrganization("Current");
        User actorUser = CreateUser("Actor");
        User targetUser = CreateUser("Target");
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actorUser.Id,
            actorRole,
            CreatedAt);
        var targetMembership = new OrganizationMembership(
            organization.Id,
            targetUser.Id,
            targetRole,
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

        if (!targetMembershipActive)
        {
            targetMembership.Deactivate();
        }

        await SeedAsync(
            organization,
            actorUser,
            targetUser,
            actorMembership,
            targetMembership);

        return new TestGraph(
            organization,
            actorUser,
            actorMembership,
            targetMembership);
    }

    private async Task<OrganizationRole> FindRoleAsync(Guid membershipId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership => membership.Id == membershipId)
            .Select(membership => membership.Role)
            .SingleAsync();
    }

    private async Task<AuditLog> FindSingleAuditLogAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuditLogs.AsNoTracking().SingleAsync();
    }

    private async Task<int> CountAuditLogsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuditLogs.CountAsync();
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

    private sealed record TestGraph(
        Organization Organization,
        User ActorUser,
        OrganizationMembership ActorMembership,
        OrganizationMembership TargetMembership);

    private sealed class CommandRecordingInterceptor : DbCommandInterceptor
    {
        private readonly List<CommandSnapshot> _commands = [];

        public IReadOnlyList<CommandSnapshot> Commands => _commands;

        public override ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            _commands.Add(new CommandSnapshot(
                command.CommandText,
                command.Parameters
                    .Cast<DbParameter>()
                    .Select(parameter => parameter.Value)
                    .ToArray()));
            return ValueTask.FromResult(result);
        }
    }

    private sealed record CommandSnapshot(
        string Text,
        IReadOnlyList<object?> ParameterValues);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
