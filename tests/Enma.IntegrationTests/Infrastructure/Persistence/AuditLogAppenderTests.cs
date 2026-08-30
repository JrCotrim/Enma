using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Enma.Application.Auditing;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuditLogAppenderTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string OriginalName = "Audit C Organization";
    private const string RenamedName = "Renamed Audit C Organization";
    private const string TraceId = "0123456789abcdef0123456789abcdef";
    private const string SpanId = "0123456789abcdef";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        27,
        10,
        0,
        0,
        TimeSpan.Zero);

    private static readonly DateTimeOffset OccurredAt = new(
        2026,
        8,
        27,
        14,
        30,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Append_InsideTransaction_TracksAuthoritativeLogWithoutSaving()
    {
        ActorGraph graph = await SeedActorGraphAsync();
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync();
        LockedActorGraph locked = await LockActorGraphAsync(dbContext, graph);
        var timeProvider = new FixedTimeProvider(OccurredAt);
        ActivityTraceId expectedTraceId = ActivityTraceId.CreateFromString(
            TraceId.AsSpan());
        ActivitySpanId parentSpanId = ActivitySpanId.CreateFromString(
            SpanId.AsSpan());
        using Activity activity = new Activity(nameof(AuditLogAppenderTests))
            .SetParentId(
                expectedTraceId,
                parentSpanId,
                ActivityTraceFlags.Recorded)
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();

        AuditLogAppender.Append(
            dbContext,
            timeProvider,
            locked.Actor,
            CreateRenameIntent(graph.Organization.Id));

        var entry = Assert.Single(dbContext.ChangeTracker.Entries<AuditLog>());
        AuditLog auditLog = entry.Entity;
        Assert.Equal(EntityState.Added, entry.State);
        Assert.NotEqual(Guid.Empty, auditLog.Id);
        Assert.Equal(locked.Actor.OrganizationId, auditLog.OrganizationId);
        Assert.Equal(locked.Actor.UserId, auditLog.ActorUserId);
        Assert.Equal(locked.Actor.MembershipId, auditLog.ActorMembershipId);
        Assert.Equal(locked.Actor.Role, auditLog.ActorRoleAtOccurrence);
        Assert.Equal(OccurredAt, auditLog.OccurredAt);
        Assert.Equal(TraceId, auditLog.TraceId);
        Assert.Equal(AuditEventType.OrganizationRenamed, auditLog.EventType);
        Assert.Equal(AuditEntityType.Organization, auditLog.EntityType);
        Assert.Equal(graph.Organization.Id, auditLog.EntityId);
        Assert.IsType<OrganizationRenamedAuditDetails>(auditLog.Details);

        await using EnmaDbContext readContext = fixture.CreateDbContext();
        Assert.Equal(0, await readContext.AuditLogs.CountAsync());

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Append_WithoutCurrentActivity_UsesNullTraceId()
    {
        ActorGraph graph = await SeedActorGraphAsync();
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync();
        LockedActorGraph locked = await LockActorGraphAsync(dbContext, graph);
        Activity? previousActivity = Activity.Current;

        try
        {
            Activity.Current = null;
            AuditLogAppender.Append(
                dbContext,
                new FixedTimeProvider(OccurredAt),
                locked.Actor,
                CreateRenameIntent(graph.Organization.Id));
        }
        finally
        {
            Activity.Current = previousActivity;
        }

        AuditLog auditLog = Assert.Single(
            dbContext.ChangeTracker.Entries<AuditLog>()).Entity;
        Assert.Null(auditLog.TraceId);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Append_WithoutActiveTransaction_FailsBeforeTracking()
    {
        ActorGraph graph = await SeedActorGraphAsync();
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        TransactionalAuditActorContext actor =
            TransactionalAuditActorContext.FromValidatedMembership(
                graph.Membership);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => AuditLogAppender.Append(
                dbContext,
                new FixedTimeProvider(OccurredAt),
                actor,
                CreateRenameIntent(graph.Organization.Id)));

        Assert.Contains("active transaction", exception.Message);
        Assert.Empty(dbContext.ChangeTracker.Entries<AuditLog>());
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task SaveAndCommit_PersistsBusinessMutationAndAuditTogether()
    {
        ActorGraph graph = await SeedActorGraphAsync();
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync();
        LockedActorGraph locked = await LockActorGraphAsync(dbContext, graph);

        locked.Organization.Rename(RenamedName);
        AuditLogAppender.Append(
            dbContext,
            new FixedTimeProvider(OccurredAt),
            locked.Actor,
            CreateRenameIntent(graph.Organization.Id));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        await using EnmaDbContext readContext = fixture.CreateDbContext();
        Organization organization = await readContext.Organizations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == graph.Organization.Id);
        AuditLog auditLog = await readContext.AuditLogs
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(RenamedName, organization.Name);
        Assert.Equal(graph.Organization.Id, auditLog.EntityId);
    }

    [Fact]
    public async Task Append_WithNewResultingMembership_PersistsAcceptedActorTogether()
    {
        ActorGraph graph = await SeedActorGraphAsync();
        var acceptedUser = new User(
            "Accepted Invitation User",
            "accepted.invitation.user@example.test",
            CreatedAt);

        await using (EnmaDbContext userContext = fixture.CreateDbContext())
        {
            userContext.Users.Add(acceptedUser);
            await userContext.SaveChangesAsync();
        }

        Guid invitationId = Guid.NewGuid();
        var resultingMembership = new OrganizationMembership(
            graph.Organization.Id,
            acceptedUser.Id,
            OrganizationRole.Member,
            OccurredAt);

        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        await using (IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync())
        {
            dbContext.OrganizationMemberships.Add(resultingMembership);
            AuditLogAppender.Append(
                dbContext,
                new FixedTimeProvider(OccurredAt),
                TransactionalAuditActorContext.FromValidatedMembership(
                    resultingMembership),
                new AuditIntent(
                    AuditEventType.OrganizationInvitationAccepted,
                    invitationId));

            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using EnmaDbContext readContext = fixture.CreateDbContext();
        AuditLog auditLog = await readContext.AuditLogs
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(resultingMembership.Id, auditLog.ActorMembershipId);
        Assert.Equal(acceptedUser.Id, auditLog.ActorUserId);
        Assert.Equal(AuditEventType.OrganizationInvitationAccepted, auditLog.EventType);
        Assert.Equal(AuditEntityType.OrganizationInvitation, auditLog.EntityType);
        Assert.Equal(invitationId, auditLog.EntityId);
        Assert.Null(auditLog.Details);
    }

    [Fact]
    public async Task SaveThenRollback_PersistsNeitherBusinessMutationNorAudit()
    {
        ActorGraph graph = await SeedActorGraphAsync();
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync();
        LockedActorGraph locked = await LockActorGraphAsync(dbContext, graph);

        locked.Organization.Rename(RenamedName);
        AuditLogAppender.Append(
            dbContext,
            new FixedTimeProvider(OccurredAt),
            locked.Actor,
            CreateRenameIntent(graph.Organization.Id));
        await dbContext.SaveChangesAsync();

        await transaction.RollbackAsync();

        await using EnmaDbContext readContext = fixture.CreateDbContext();
        Organization organization = await readContext.Organizations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == graph.Organization.Id);

        Assert.Equal(OriginalName, organization.Name);
        Assert.Equal(0, await readContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task AuditInsertFailure_RollsBackBusinessMutationAndAudit()
    {
        ActorGraph graph = await SeedActorGraphAsync();
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync();
        LockedActorGraph locked = await LockActorGraphAsync(dbContext, graph);

        locked.Organization.Rename(RenamedName);
        AuditLogAppender.Append(
            dbContext,
            new FixedTimeProvider(OccurredAt),
            locked.Actor,
            CreateRenameIntent(graph.Organization.Id));
        AuditLog auditLog = Assert.Single(
            dbContext.ChangeTracker.Entries<AuditLog>()).Entity;
        dbContext.Entry(auditLog)
            .Property<string?>("_detailsJson")
            .CurrentValue = null;

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());
        PostgresException postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal(
            "ck_audit_logs_details_contract",
            postgresException.ConstraintName);

        await transaction.RollbackAsync();

        await using EnmaDbContext readContext = fixture.CreateDbContext();
        Organization organization = await readContext.Organizations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == graph.Organization.Id);

        Assert.Equal(OriginalName, organization.Name);
        Assert.Equal(0, await readContext.AuditLogs.CountAsync());
    }

    [Fact]
    public void RecordingBoundary_RemainsInfrastructureOnly()
    {
        string[] auditIntentProperties = typeof(AuditIntent)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        AssemblyName[] applicationReferences = typeof(AuditIntent)
            .Assembly
            .GetReferencedAssemblies();
        MethodInfo authoritativeFactory = typeof(AuditLog).GetMethod(
            "CreateAuthoritative",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "AuditLog authoritative factory was not found.");
        string[] domainFriendAssemblies = typeof(AuditLog)
            .Assembly
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName.Split(',')[0])
            .ToArray();

        Assert.Equal(
            ["Details", "EntityId", "EntityType", "EventType"],
            auditIntentProperties);
        Assert.DoesNotContain(
            applicationReferences,
            reference => reference.Name == "Enma.Infrastructure");
        Assert.True(authoritativeFactory.IsAssembly);
        Assert.DoesNotContain("Enma.Application", domainFriendAssemblies);
        Assert.True(typeof(TransactionalAuditActorContext).IsNotPublic);
        Assert.True(typeof(AuditLogAppender).IsNotPublic);
    }

    private async Task<ActorGraph> SeedActorGraphAsync()
    {
        var organization = new Organization(
            OriginalName,
            "audit-c-organization",
            CreatedAt);
        var user = new User(
            "Audit C Actor",
            "audit.c.actor@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Administrator,
            CreatedAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(organization, user, membership);
        await dbContext.SaveChangesAsync();

        return new ActorGraph(organization, user, membership);
    }

    private static async Task<LockedActorGraph> LockActorGraphAsync(
        EnmaDbContext dbContext,
        ActorGraph graph)
    {
        Organization organization = await dbContext.Organizations
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organizations
                WHERE id = {graph.Organization.Id}
                FOR UPDATE
                """)
            .SingleAsync();
        OrganizationMembership membership = await dbContext
            .OrganizationMemberships
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organization_memberships
                WHERE organization_id = {graph.Organization.Id}
                  AND id = {graph.Membership.Id}
                FOR UPDATE
                """)
            .SingleAsync();
        User user = await dbContext.Users
            .FromSqlInterpolated(
                $"""
                SELECT * FROM users
                WHERE id = {graph.User.Id}
                FOR UPDATE
                """)
            .SingleAsync();

        Assert.True(organization.IsActive);
        Assert.True(membership.IsActive);
        Assert.True(user.IsActive);
        Assert.Equal(organization.Id, membership.OrganizationId);
        Assert.Equal(user.Id, membership.UserId);

        return new LockedActorGraph(
            organization,
            TransactionalAuditActorContext.FromValidatedMembership(
                membership));
    }

    private static AuditIntent CreateRenameIntent(Guid organizationId)
    {
        return new AuditIntent(
            AuditEventType.OrganizationRenamed,
            organizationId,
            new OrganizationRenamedAuditDetails(OriginalName, RenamedName));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record ActorGraph(
        Organization Organization,
        User User,
        OrganizationMembership Membership);

    private sealed record LockedActorGraph(
        Organization Organization,
        TransactionalAuditActorContext Actor);
}
