using System.Data.Common;
using Enma.Application.Organizations.Members.Ownership;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationOwnershipTransferPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        10,
        5,
        12,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateTimeOffset OccurredAt = CreatedAt.AddHours(3);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ExecuteAsync_ActiveVerifiedAdministrator_SwapsRolesAndAudits()
    {
        TestGraph graph = await SeedGraphAsync();

        OrganizationOwnershipTransferPersistenceResult result =
            await CreatePersistence().ExecuteAsync(CreateRequest(graph));

        Assert.Equal(OrganizationOwnershipTransferPersistenceResult.Succeeded, result);
        Assert.Equal(
            OrganizationRole.Administrator,
            await FindRoleAsync(graph.ActorMembership.Id));
        Assert.Equal(
            OrganizationRole.Owner,
            await FindRoleAsync(graph.TargetMembership.Id));
        Assert.Equal(
            [graph.TargetMembership.Id],
            await FindActiveOwnerMembershipIdsAsync(graph.Organization.Id));
        AuditLog auditLog = await FindSingleAuditLogAsync();
        Assert.Equal(graph.Organization.Id, auditLog.OrganizationId);
        Assert.Equal(graph.ActorUser.Id, auditLog.ActorUserId);
        Assert.Equal(graph.ActorMembership.Id, auditLog.ActorMembershipId);
        Assert.Equal(OrganizationRole.Owner, auditLog.ActorRoleAtOccurrence);
        Assert.Equal(
            AuditEventType.OrganizationOwnershipTransferred,
            auditLog.EventType);
        Assert.Equal(AuditEntityType.Organization, auditLog.EntityType);
        Assert.Equal(graph.Organization.Id, auditLog.EntityId);
        Assert.Equal(OccurredAt, auditLog.OccurredAt);
        OrganizationOwnershipTransferredAuditDetails details =
            Assert.IsType<OrganizationOwnershipTransferredAuditDetails>(
                auditLog.Details);
        Assert.Equal(graph.ActorMembership.Id, details.PreviousOwnerMembershipId);
        Assert.Equal(graph.TargetMembership.Id, details.NewOwnerMembershipId);
    }

    [Fact]
    public async Task ExecuteAsync_AuditInsertFailure_RollsBackDemotionAndPromotion()
    {
        TestGraph graph = await SeedGraphAsync();

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => CreatePersistence(new InvalidAuditDetailsInterceptor())
                .ExecuteAsync(CreateRequest(graph)));

        PostgresException postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal(
            "ck_audit_logs_details_contract",
            postgresException.ConstraintName);
        await AssertUnchangedAsync(graph);
        Assert.Equal(
            [graph.ActorMembership.Id],
            await FindActiveOwnerMembershipIdsAsync(graph.Organization.Id));
    }

    [Theory]
    [InlineData(TargetState.InactiveMembership)]
    [InlineData(TargetState.InactiveUser)]
    [InlineData(TargetState.UnverifiedEmail)]
    [InlineData(TargetState.Member)]
    [InlineData(TargetState.ExpectedRoleMismatch)]
    public async Task ExecuteAsync_UnavailableTarget_ReturnsTargetUnavailableWithoutWrite(
        TargetState targetState)
    {
        TestGraph graph = await SeedGraphAsync(
            targetRole: targetState == TargetState.Member
                ? OrganizationRole.Member
                : OrganizationRole.Administrator,
            targetMembershipActive: targetState != TargetState.InactiveMembership,
            targetUserActive: targetState != TargetState.InactiveUser,
            targetEmailVerified: targetState != TargetState.UnverifiedEmail);
        OrganizationRole expectedTargetRole =
            targetState == TargetState.ExpectedRoleMismatch
                ? OrganizationRole.Member
                : OrganizationRole.Administrator;

        OrganizationOwnershipTransferPersistenceResult result =
            await CreatePersistence().ExecuteAsync(
                CreateRequest(graph) with
                {
                    ExpectedTargetRole = expectedTargetRole
                });

        Assert.Equal(
            OrganizationOwnershipTransferPersistenceResult.TargetUnavailable,
            result);
        await AssertUnchangedAsync(graph);
    }

    [Fact]
    public async Task ExecuteAsync_TargetIsActor_ReturnsTargetUnavailableWithoutWrite()
    {
        TestGraph graph = await SeedGraphAsync();

        OrganizationOwnershipTransferPersistenceResult result =
            await CreatePersistence().ExecuteAsync(
                CreateRequest(graph) with
                {
                    TargetMembershipId = graph.ActorMembership.Id
                });

        Assert.Equal(
            OrganizationOwnershipTransferPersistenceResult.TargetUnavailable,
            result);
        await AssertUnchangedAsync(graph);
    }

    [Fact]
    public async Task ExecuteAsync_NonexistentTarget_ReturnsNotFoundWithoutWrite()
    {
        TestGraph graph = await SeedGraphAsync();

        OrganizationOwnershipTransferPersistenceResult result =
            await CreatePersistence().ExecuteAsync(
                CreateRequest(graph) with
                {
                    TargetMembershipId = Guid.NewGuid()
                });

        Assert.Equal(OrganizationOwnershipTransferPersistenceResult.NotFound, result);
        await AssertUnchangedAsync(graph);
    }

    [Fact]
    public async Task ExecuteAsync_ForeignTenantAdministrator_ReturnsNotFoundWithoutWrite()
    {
        TestGraph graph = await SeedGraphAsync();
        TestGraph foreign = await SeedGraphAsync("Foreign");

        OrganizationOwnershipTransferPersistenceResult result =
            await CreatePersistence().ExecuteAsync(
                CreateRequest(graph) with
                {
                    TargetMembershipId = foreign.TargetMembership.Id
                });

        Assert.Equal(OrganizationOwnershipTransferPersistenceResult.NotFound, result);
        await AssertUnchangedAsync(graph);
        await AssertUnchangedAsync(foreign);
    }

    [Fact]
    public async Task ExecuteAsync_ForeignOrganizationContextForOwnMembership_DeniesWithoutWrite()
    {
        TestGraph graph = await SeedGraphAsync();
        TestGraph foreign = await SeedGraphAsync("Foreign");

        OrganizationOwnershipTransferPersistenceResult result =
            await CreatePersistence().ExecuteAsync(
                CreateRequest(graph) with
                {
                    OrganizationId = foreign.Organization.Id
                });

        Assert.Equal(
            OrganizationOwnershipTransferPersistenceResult.AccessDenied,
            result);
        await AssertUnchangedAsync(graph);
        await AssertUnchangedAsync(foreign);
    }

    [Theory]
    [InlineData(ActorState.Administrator)]
    [InlineData(ActorState.Member)]
    [InlineData(ActorState.InactiveUser)]
    [InlineData(ActorState.InactiveOrganization)]
    public async Task ExecuteAsync_UnavailableLiveActor_DeniesWithoutWrite(
        ActorState actorState)
    {
        TestGraph graph = await SeedGraphAsync(
            actorRole: actorState switch
            {
                ActorState.Administrator => OrganizationRole.Administrator,
                ActorState.Member => OrganizationRole.Member,
                _ => OrganizationRole.Owner
            },
            actorUserActive: actorState != ActorState.InactiveUser,
            organizationActive: actorState != ActorState.InactiveOrganization);

        OrganizationOwnershipTransferPersistenceResult result =
            await CreatePersistence().ExecuteAsync(CreateRequest(graph));

        Assert.Equal(
            OrganizationOwnershipTransferPersistenceResult.AccessDenied,
            result);
        await AssertUnchangedAsync(graph);
    }

    [Fact]
    public async Task ExecuteAsync_ActorMembershipOfAnotherUser_DeniesWithoutWrite()
    {
        TestGraph graph = await SeedGraphAsync();

        OrganizationOwnershipTransferPersistenceResult result =
            await CreatePersistence().ExecuteAsync(
                CreateRequest(graph) with
                {
                    UserId = graph.TargetMembership.UserId
                });

        Assert.Equal(
            OrganizationOwnershipTransferPersistenceResult.AccessDenied,
            result);
        await AssertUnchangedAsync(graph);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData((OrganizationRole)999)]
    public async Task ExecuteAsync_UnsupportedExpectedRole_FailsClosedWithoutWrite(
        OrganizationRole expectedTargetRole)
    {
        TestGraph graph = await SeedGraphAsync();

        OrganizationOwnershipTransferPersistenceResult result =
            await CreatePersistence().ExecuteAsync(
                CreateRequest(graph) with
                {
                    ExpectedTargetRole = expectedTargetRole
                });

        Assert.Equal(
            OrganizationOwnershipTransferPersistenceResult.InvalidInput,
            result);
        await AssertUnchangedAsync(graph);
    }

    [Fact]
    public async Task ExecuteAsync_LocksAndWritesInDeterministicOrder()
    {
        TestGraph graph = await SeedGraphAsync();
        var interceptor = new CommandRecordingInterceptor();

        OrganizationOwnershipTransferPersistenceResult result =
            await CreatePersistence(interceptor).ExecuteAsync(CreateRequest(graph));

        Assert.Equal(OrganizationOwnershipTransferPersistenceResult.Succeeded, result);
        IReadOnlyList<CommandSnapshot> commands = interceptor.Commands;
        int organizationLock = Assert.Single(
            IndexesOf(commands, "FROM organizations"));
        int membershipLock = Assert.Single(
            IndexesOf(commands, "FROM organization_memberships"));
        int userLock = Assert.Single(IndexesOf(commands, "FROM users"));
        int[] membershipUpdates = IndexesOf(
            commands,
            "UPDATE organization_memberships");
        int auditInsert = Assert.Single(IndexesOf(commands, "INSERT INTO audit_logs"));

        Assert.True(organizationLock < membershipLock);
        Assert.True(membershipLock < userLock);
        Assert.Contains("FOR UPDATE", commands[organizationLock].Text);
        Assert.Contains(
            graph.Organization.Id,
            commands[organizationLock].ParameterValues);
        CommandSnapshot membershipCommand = commands[membershipLock];
        Assert.Contains("organization_id", membershipCommand.Text);
        Assert.Contains("id = ANY", membershipCommand.Text);
        Assert.Contains("ORDER BY id", membershipCommand.Text);
        Assert.Contains("FOR UPDATE NOWAIT", membershipCommand.Text);
        Assert.Contains(graph.Organization.Id, membershipCommand.ParameterValues);
        Assert.Equal(
            new[] { graph.ActorMembership.Id, graph.TargetMembership.Id }
                .OrderBy(id => id),
            Assert.IsType<Guid[]>(Assert.Single(
                membershipCommand.ParameterValues,
                value => value is Guid[])));
        CommandSnapshot userCommand = commands[userLock];
        Assert.Contains("ORDER BY id", userCommand.Text);
        Assert.Contains("FOR UPDATE", userCommand.Text);
        Assert.Equal(
            new[] { graph.ActorUser.Id, graph.TargetMembership.UserId }
                .OrderBy(id => id),
            Assert.IsType<Guid[]>(Assert.Single(
                userCommand.ParameterValues,
                value => value is Guid[])));

        // Separate statements: demote the actor, promote the target, then audit.
        Assert.Equal(2, membershipUpdates.Length);
        Assert.True(userLock < membershipUpdates[0]);
        Assert.True(membershipUpdates[0] < membershipUpdates[1]);
        Assert.True(membershipUpdates[1] < auditInsert);
        Assert.Contains(
            graph.ActorMembership.Id,
            commands[membershipUpdates[0]].ParameterValues);
        Assert.Contains(
            (int)OrganizationRole.Administrator,
            commands[membershipUpdates[0]].ParameterValues);
        Assert.Contains(
            graph.TargetMembership.Id,
            commands[membershipUpdates[1]].ParameterValues);
        Assert.Contains(
            (int)OrganizationRole.Owner,
            commands[membershipUpdates[1]].ParameterValues);
    }

    private OrganizationOwnershipTransferPersistence CreatePersistence(
        IInterceptor? interceptor = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString);

        if (interceptor is not null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        return new OrganizationOwnershipTransferPersistence(
            optionsBuilder.Options,
            new FixedTimeProvider(OccurredAt));
    }

    private static OrganizationOwnershipTransferPersistenceRequest CreateRequest(
        TestGraph graph)
    {
        return new OrganizationOwnershipTransferPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.TargetMembership.Id,
            OrganizationRole.Administrator);
    }

    private async Task<TestGraph> SeedGraphAsync(
        string marker = "Current",
        OrganizationRole actorRole = OrganizationRole.Owner,
        bool actorUserActive = true,
        bool organizationActive = true,
        OrganizationRole targetRole = OrganizationRole.Administrator,
        bool targetMembershipActive = true,
        bool targetUserActive = true,
        bool targetEmailVerified = true)
    {
        Organization organization = CreateOrganization(marker);
        User actorUser = CreateUser($"{marker} Actor");
        User targetUser = CreateUser($"{marker} Target");
        actorUser.VerifyEmail(CreatedAt);

        if (targetEmailVerified)
        {
            targetUser.VerifyEmail(CreatedAt);
        }

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

        if (!targetUserActive)
        {
            targetUser.Deactivate();
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

    private async Task AssertUnchangedAsync(TestGraph graph)
    {
        Assert.Equal(
            graph.ActorMembership.Role,
            await FindRoleAsync(graph.ActorMembership.Id));
        Assert.Equal(
            graph.TargetMembership.Role,
            await FindRoleAsync(graph.TargetMembership.Id));
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.False(await dbContext.AuditLogs.AnyAsync(
            auditLog => auditLog.OrganizationId == graph.Organization.Id));
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

    private async Task<Guid[]> FindActiveOwnerMembershipIdsAsync(Guid organizationId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership =>
                membership.OrganizationId == organizationId &&
                membership.Role == OrganizationRole.Owner &&
                membership.IsActive)
            .Select(membership => membership.Id)
            .ToArrayAsync();
    }

    private async Task<AuditLog> FindSingleAuditLogAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.AuditLogs.AsNoTracking().SingleAsync();
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static int[] IndexesOf(
        IReadOnlyList<CommandSnapshot> commands,
        string fragment)
    {
        return commands
            .Select((command, index) => (command, index))
            .Where(item => item.command.Text.Contains(
                fragment,
                StringComparison.Ordinal))
            .Select(item => item.index)
            .ToArray();
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

    public enum TargetState
    {
        InactiveMembership = 0,
        InactiveUser = 1,
        UnverifiedEmail = 2,
        Member = 3,
        ExpectedRoleMismatch = 4
    }

    public enum ActorState
    {
        Administrator = 0,
        Member = 1,
        InactiveUser = 2,
        InactiveOrganization = 3
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
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command)
        {
            _commands.Add(new CommandSnapshot(
                command.CommandText,
                command.Parameters
                    .Cast<DbParameter>()
                    .Select(parameter => parameter.Value)
                    .ToArray()));
        }
    }

    private sealed record CommandSnapshot(
        string Text,
        IReadOnlyList<object?> ParameterValues);

    // Breaks only the final save, after the demotion and promotion statements.
    private sealed class InvalidAuditDetailsInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            EnmaDbContext dbContext = Assert.IsType<EnmaDbContext>(eventData.Context);
            AuditLog? auditLog = dbContext.ChangeTracker.Entries<AuditLog>()
                .SingleOrDefault(entry => entry.State == EntityState.Added)
                ?.Entity;

            if (auditLog is not null)
            {
                dbContext.Entry(auditLog)
                    .Property<string?>("_detailsJson")
                    .CurrentValue = null;
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
