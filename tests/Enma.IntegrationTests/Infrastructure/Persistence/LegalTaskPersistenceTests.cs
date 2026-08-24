using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalTaskPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string AssigneeForeignKey =
        "fk_legal_tasks_memberships_org_assignee_membership_id";
    private const string CreatorForeignKey =
        "fk_legal_tasks_memberships_org_created_by_membership_id";
    private const string OrganizationForeignKey =
        "fk_legal_tasks_organizations_organization_id";
    private const string ProcessForeignKey =
        "fk_legal_tasks_legal_processes_organization_id_process_id";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        14,
        15,
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
    public async Task SaveChangesAsync_WithMinimalLegalTask_PreservesNullableFields()
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        var legalTask = new LegalTask(
            graph.Organization.Id,
            "Minimal Task",
            null,
            null,
            null,
            null,
            graph.CreatorMembership.Id,
            CreatedAt);

        await SeedAsync(
            graph.Organization,
            graph.CreatorUser,
            graph.CreatorMembership,
            legalTask);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        LegalTask persistedLegalTask = await dbContext.LegalTasks
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(legalTask.Id, persistedLegalTask.Id);
        Assert.Equal(graph.Organization.Id, persistedLegalTask.OrganizationId);
        Assert.Equal("Minimal Task", persistedLegalTask.Title);
        Assert.Null(persistedLegalTask.Description);
        Assert.Null(persistedLegalTask.DueDate);
        Assert.Null(persistedLegalTask.ProcessId);
        Assert.Null(persistedLegalTask.AssigneeMembershipId);
        Assert.Equal(
            graph.CreatorMembership.Id,
            persistedLegalTask.CreatedByMembershipId);
        Assert.NotEqual(
            graph.CreatorUser.Id,
            persistedLegalTask.CreatedByMembershipId);
        Assert.Equal(CreatedAt, persistedLegalTask.CreatedAt);
        Assert.Null(persistedLegalTask.CompletedAt);
    }

    [Theory]
    [MemberData(nameof(RoundTripDueDates))]
    public async Task SaveChangesAsync_WithFullLegalTask_RoundTripsExactDateOnly(
        DateOnly? dueDate)
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        var legalTask = new LegalTask(
            graph.Organization.Id,
            "  Prepare Defense  ",
            "  Review documents  ",
            dueDate,
            graph.LegalProcess.Id,
            graph.AssigneeMembership.Id,
            graph.CreatorMembership.Id,
            CreatedAt);

        await SeedGraphAndTaskAsync(graph, legalTask);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        LegalTask persistedLegalTask = await dbContext.LegalTasks
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal("Prepare Defense", persistedLegalTask.Title);
        Assert.Equal("Review documents", persistedLegalTask.Description);
        Assert.Equal(dueDate, persistedLegalTask.DueDate);
        Assert.Equal(graph.LegalProcess.Id, persistedLegalTask.ProcessId);
        Assert.Equal(
            graph.AssigneeMembership.Id,
            persistedLegalTask.AssigneeMembershipId);
        Assert.NotEqual(
            graph.AssigneeUser.Id,
            persistedLegalTask.AssigneeMembershipId);
    }

    [Fact]
    public async Task SaveChangesAsync_AcrossLifecycle_PersistsCompleteAndReopen()
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        LegalTask legalTask = CreateFullLegalTask(graph);
        await SeedGraphAndTaskAsync(graph, legalTask);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        LegalTask persistedLegalTask = await dbContext.LegalTasks.SingleAsync();
        DateTimeOffset completedAt = CreatedAt.AddDays(1);

        persistedLegalTask.Complete(completedAt);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        persistedLegalTask = await dbContext.LegalTasks.SingleAsync();
        Assert.Equal(completedAt, persistedLegalTask.CompletedAt);

        persistedLegalTask.Reopen();
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        persistedLegalTask = await dbContext.LegalTasks.SingleAsync();
        Assert.Null(persistedLegalTask.CompletedAt);
    }

    [Fact]
    public async Task SaveChangesAsync_WithMissingOrganization_RejectsLegalTask()
    {
        var legalTask = new LegalTask(
            Guid.NewGuid(),
            "Missing Organization",
            null,
            null,
            null,
            null,
            Guid.NewGuid(),
            CreatedAt);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.LegalTasks.Add(legalTask);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        PostgresException postgresException =
            Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgresException.SqlState);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenDeletingOrganizationWithLegalTask_RestrictsDelete()
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        LegalTask legalTask = CreateFullLegalTask(graph);
        await SeedGraphAndTaskAsync(graph, legalTask);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.Organizations
                .Where(organization => organization.Id == graph.Organization.Id)
                .ExecuteDeleteAsync());
        Assert.Equal(PostgresErrorCodes.RestrictViolation, exception.SqlState);
        Assert.Equal(1, await dbContext.LegalTasks.CountAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_WithCrossTenantProcess_RejectsLegalTask()
    {
        TenantGraph graphA = CreateTenantGraph("Alpha", "alpha");
        TenantGraph graphB = CreateTenantGraph("Beta", "beta");
        await SeedAsync(GetGraphEntities(graphA).Concat(GetGraphEntities(graphB)).ToArray());
        var legalTask = new LegalTask(
            graphA.Organization.Id,
            "Cross-tenant Process",
            null,
            null,
            graphB.LegalProcess.Id,
            null,
            graphA.CreatorMembership.Id,
            CreatedAt);

        await AssertForeignKeyViolationAsync(legalTask, ProcessForeignKey);
    }

    [Fact]
    public async Task SaveChangesAsync_WithMissingProcess_RejectsLegalTask()
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        await SeedAsync(
            graph.Organization,
            graph.CreatorUser,
            graph.CreatorMembership);
        var legalTask = new LegalTask(
            graph.Organization.Id,
            "Missing Process",
            null,
            null,
            Guid.NewGuid(),
            null,
            graph.CreatorMembership.Id,
            CreatedAt);

        await AssertForeignKeyViolationAsync(legalTask, ProcessForeignKey);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenDeletingReferencedProcess_RestrictsDelete()
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        LegalTask legalTask = CreateFullLegalTask(graph);
        await SeedGraphAndTaskAsync(graph, legalTask);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.LegalProcesses
                .Where(legalProcess => legalProcess.Id == graph.LegalProcess.Id)
                .ExecuteDeleteAsync());

        Assert.Equal(PostgresErrorCodes.RestrictViolation, exception.SqlState);
        Assert.Equal(ProcessForeignKey, exception.ConstraintName);
        Assert.Equal(1, await dbContext.LegalTasks.CountAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_WithCrossTenantCreatorMembership_RejectsLegalTask()
    {
        TenantGraph graphA = CreateTenantGraph("Alpha", "alpha");
        TenantGraph graphB = CreateTenantGraph("Beta", "beta");
        await SeedAsync(GetGraphEntities(graphA).Concat(GetGraphEntities(graphB)).ToArray());
        var legalTask = new LegalTask(
            graphA.Organization.Id,
            "Cross-tenant Creator",
            null,
            null,
            null,
            null,
            graphB.CreatorMembership.Id,
            CreatedAt);

        await AssertForeignKeyViolationAsync(legalTask, CreatorForeignKey);
    }

    [Fact]
    public async Task SaveChangesAsync_WithMissingCreatorMembership_RejectsLegalTask()
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        await SeedAsync(graph.Organization);
        var legalTask = new LegalTask(
            graph.Organization.Id,
            "Missing Creator",
            null,
            null,
            null,
            null,
            Guid.NewGuid(),
            CreatedAt);

        await AssertForeignKeyViolationAsync(legalTask, CreatorForeignKey);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenDeletingCreatorMembership_RestrictsDelete()
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        LegalTask legalTask = CreateFullLegalTask(graph);
        await SeedGraphAndTaskAsync(graph, legalTask);

        await AssertMembershipDeleteRestrictedAsync(
            graph.CreatorMembership.Id,
            CreatorForeignKey);
    }

    [Fact]
    public async Task SaveChangesAsync_WithCrossTenantAssigneeMembership_RejectsLegalTask()
    {
        TenantGraph graphA = CreateTenantGraph("Alpha", "alpha");
        TenantGraph graphB = CreateTenantGraph("Beta", "beta");
        await SeedAsync(GetGraphEntities(graphA).Concat(GetGraphEntities(graphB)).ToArray());
        var legalTask = new LegalTask(
            graphA.Organization.Id,
            "Cross-tenant Assignee",
            null,
            null,
            null,
            graphB.AssigneeMembership.Id,
            graphA.CreatorMembership.Id,
            CreatedAt);

        await AssertForeignKeyViolationAsync(legalTask, AssigneeForeignKey);
    }

    [Fact]
    public async Task SaveChangesAsync_WithMissingAssigneeMembership_RejectsLegalTask()
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        await SeedAsync(
            graph.Organization,
            graph.CreatorUser,
            graph.CreatorMembership);
        var legalTask = new LegalTask(
            graph.Organization.Id,
            "Missing Assignee",
            null,
            null,
            null,
            Guid.NewGuid(),
            graph.CreatorMembership.Id,
            CreatedAt);

        await AssertForeignKeyViolationAsync(legalTask, AssigneeForeignKey);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenDeletingAssigneeMembership_RestrictsDelete()
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        LegalTask legalTask = CreateFullLegalTask(graph);
        await SeedGraphAndTaskAsync(graph, legalTask);

        await AssertMembershipDeleteRestrictedAsync(
            graph.AssigneeMembership.Id,
            AssigneeForeignKey);
    }

    [Fact]
    public async Task SaveChangesAsync_AfterMembershipAndUserDeactivation_PreservesHistoricalTask()
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        LegalTask legalTask = CreateFullLegalTask(graph);
        await SeedGraphAndTaskAsync(graph, legalTask);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembership[] memberships =
            await dbContext.OrganizationMemberships.ToArrayAsync();
        User[] users = await dbContext.Users.ToArrayAsync();

        foreach (OrganizationMembership membership in memberships)
        {
            membership.Deactivate();
        }

        foreach (User user in users)
        {
            user.Deactivate();
        }

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        LegalTask persistedLegalTask = await dbContext.LegalTasks
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(legalTask.Id, persistedLegalTask.Id);
        Assert.Equal(
            graph.CreatorMembership.Id,
            persistedLegalTask.CreatedByMembershipId);
        Assert.Equal(
            graph.AssigneeMembership.Id,
            persistedLegalTask.AssigneeMembershipId);
        Assert.All(
            await dbContext.OrganizationMemberships.AsNoTracking().ToArrayAsync(),
            membership => Assert.False(membership.IsActive));
        Assert.All(
            await dbContext.Users.AsNoTracking().ToArrayAsync(),
            user => Assert.False(user.IsActive));
    }

    [Fact]
    public async Task SaveChangesAsync_AfterClientDeactivation_PreservesProcessLinkedTask()
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        LegalTask legalTask = CreateFullLegalTask(graph);
        await SeedGraphAndTaskAsync(graph, legalTask);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Client client = await dbContext.Clients.SingleAsync();

        client.Deactivate();
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        Assert.False((await dbContext.Clients.SingleAsync()).IsActive);
        LegalTask persistedLegalTask = await dbContext.LegalTasks.SingleAsync();
        Assert.Equal(legalTask.Id, persistedLegalTask.Id);
        Assert.Equal(graph.LegalProcess.Id, persistedLegalTask.ProcessId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("  Padded Title  ")]
    public async Task DatabaseInsert_WithUnnormalizedTitle_EnforcesCheckConstraint(
        string title)
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        await SeedAsync(
            graph.Organization,
            graph.CreatorUser,
            graph.CreatorMembership);

        PostgresException exception = await AssertInvalidDirectInsertAsync(
            graph,
            title,
            null,
            null);

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_legal_tasks_title_normalized", exception.ConstraintName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("  Padded Description  ")]
    public async Task DatabaseInsert_WithUnnormalizedDescription_EnforcesCheckConstraint(
        string description)
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        await SeedAsync(
            graph.Organization,
            graph.CreatorUser,
            graph.CreatorMembership);

        PostgresException exception = await AssertInvalidDirectInsertAsync(
            graph,
            "Valid Title",
            description,
            null);

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(
            "ck_legal_tasks_description_normalized",
            exception.ConstraintName);
    }

    [Fact]
    public async Task DatabaseInsert_WithCompletionBeforeCreation_EnforcesCheckConstraint()
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        await SeedAsync(
            graph.Organization,
            graph.CreatorUser,
            graph.CreatorMembership);

        PostgresException exception = await AssertInvalidDirectInsertAsync(
            graph,
            "Valid Title",
            null,
            CreatedAt.AddTicks(-1));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_legal_tasks_completion", exception.ConstraintName);
    }

    [Fact]
    public void LegalTaskModel_WithLockedShape_HasExpectedRelationalMetadata()
    {
        using EnmaDbContext dbContext = fixture.CreateDbContext();
        IModel designTimeModel = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType? entityType = designTimeModel.FindEntityType(typeof(LegalTask));

        Assert.NotNull(entityType);
        Assert.Equal("legal_tasks", entityType.GetTableName());
        Assert.Equal(
            [
                nameof(LegalTask.Id),
                nameof(LegalTask.OrganizationId),
                nameof(LegalTask.Title),
                nameof(LegalTask.Description),
                nameof(LegalTask.DueDate),
                nameof(LegalTask.ProcessId),
                nameof(LegalTask.AssigneeMembershipId),
                nameof(LegalTask.CreatedByMembershipId),
                nameof(LegalTask.CreatedAt),
                nameof(LegalTask.CompletedAt)
            ],
            entityType.GetProperties()
                .Select(property => property.Name)
                .OrderBy(PropertyOrder)
                .ToArray());
        Assert.Null(entityType.FindProperty("ClientId"));
        Assert.Null(entityType.FindProperty("Status"));
        Assert.Null(entityType.FindProperty("State"));
        Assert.Null(entityType.FindProperty("IsCompleted"));
        Assert.Null(entityType.FindProperty("Priority"));
        Assert.Null(entityType.FindProperty("TimeZone"));
        Assert.Null(entityType.FindProperty("DeletedAt"));
        Assert.Null(entityType.FindProperty("ReminderAt"));
        Assert.Empty(entityType.GetNavigations());

        Assert.Equal(
            "date",
            entityType.FindProperty(nameof(LegalTask.DueDate))!.GetColumnType());
        Assert.True(entityType.FindProperty(nameof(LegalTask.DueDate))!.IsNullable);
        Assert.Equal(
            150,
            entityType.FindProperty(nameof(LegalTask.Title))!.GetMaxLength());
        Assert.Equal(
            2_000,
            entityType.FindProperty(nameof(LegalTask.Description))!.GetMaxLength());
        Assert.Equal(
            "timestamp with time zone",
            entityType.FindProperty(nameof(LegalTask.CreatedAt))!.GetColumnType());
        Assert.Equal(
            "timestamp with time zone",
            entityType.FindProperty(nameof(LegalTask.CompletedAt))!.GetColumnType());

        IKey tenantIdentityKey = Assert.Single(
            entityType.GetKeys(),
            key => key.GetName() == "ak_legal_tasks_organization_id_id");
        Assert.Equal(
            [nameof(LegalTask.OrganizationId), nameof(LegalTask.Id)],
            tenantIdentityKey.Properties
                .Select(property => property.Name)
                .ToArray());

        AssertIndex(
            entityType,
            "ix_legal_tasks_pending_organization_due_date_created_at_id",
            [
                nameof(LegalTask.OrganizationId),
                nameof(LegalTask.DueDate),
                nameof(LegalTask.CreatedAt),
                nameof(LegalTask.Id)
            ],
            [false, false, true, false],
            "completed_at IS NULL");
        AssertIndex(
            entityType,
            "ix_legal_tasks_completed_organization_completed_at_id",
            [
                nameof(LegalTask.OrganizationId),
                nameof(LegalTask.CompletedAt),
                nameof(LegalTask.Id)
            ],
            [false, true, false],
            "completed_at IS NOT NULL");
        AssertIndex(
            entityType,
            "ix_legal_tasks_pending_org_process_due_date_created_at_id",
            [
                nameof(LegalTask.OrganizationId),
                nameof(LegalTask.ProcessId),
                nameof(LegalTask.DueDate),
                nameof(LegalTask.CreatedAt),
                nameof(LegalTask.Id)
            ],
            [false, false, false, true, false],
            "completed_at IS NULL");
        AssertIndex(
            entityType,
            "ix_legal_tasks_pending_org_assignee_due_date_created_at_id",
            [
                nameof(LegalTask.OrganizationId),
                nameof(LegalTask.AssigneeMembershipId),
                nameof(LegalTask.DueDate),
                nameof(LegalTask.CreatedAt),
                nameof(LegalTask.Id)
            ],
            [false, false, false, true, false],
            "completed_at IS NULL");
        AssertIndex(
            entityType,
            "ix_legal_tasks_organization_id_created_by_membership_id",
            [
                nameof(LegalTask.OrganizationId),
                nameof(LegalTask.CreatedByMembershipId)
            ],
            null,
            null);
        AssertIndex(
            entityType,
            "ix_legal_tasks_pending_due_date_organization_id_id",
            [
                nameof(LegalTask.DueDate),
                nameof(LegalTask.OrganizationId),
                nameof(LegalTask.Id)
            ],
            null,
            "completed_at IS NULL AND due_date IS NOT NULL");
        Assert.Equal(6, entityType.GetIndexes().Count());

        AssertForeignKey(
            entityType,
            OrganizationForeignKey,
            typeof(Organization),
            [nameof(LegalTask.OrganizationId)],
            [nameof(Organization.Id)]);
        AssertForeignKey(
            entityType,
            ProcessForeignKey,
            typeof(LegalProcess),
            [nameof(LegalTask.OrganizationId), nameof(LegalTask.ProcessId)],
            [nameof(LegalProcess.OrganizationId), nameof(LegalProcess.Id)]);
        AssertForeignKey(
            entityType,
            CreatorForeignKey,
            typeof(OrganizationMembership),
            [
                nameof(LegalTask.OrganizationId),
                nameof(LegalTask.CreatedByMembershipId)
            ],
            [
                nameof(OrganizationMembership.OrganizationId),
                nameof(OrganizationMembership.Id)
            ]);
        AssertForeignKey(
            entityType,
            AssigneeForeignKey,
            typeof(OrganizationMembership),
            [
                nameof(LegalTask.OrganizationId),
                nameof(LegalTask.AssigneeMembershipId)
            ],
            [
                nameof(OrganizationMembership.OrganizationId),
                nameof(OrganizationMembership.Id)
            ]);
        Assert.Equal(4, entityType.GetForeignKeys().Count());
    }

    [Fact]
    public async Task PostgreSqlSchema_WithLegalTasks_HasExpectedShapeAndIndexes()
    {
        Assert.Equal(
            "id,organization_id,title,description,due_date,process_id," +
            "assignee_membership_id,created_by_membership_id,created_at,completed_at",
            await GetTableColumnsAsync());
        Assert.Equal("date", await GetColumnTypeAsync("due_date"));
        Assert.Equal("timestamp with time zone", await GetColumnTypeAsync("created_at"));
        Assert.Equal("timestamp with time zone", await GetColumnTypeAsync("completed_at"));

        Assert.Equal(
            "organization_id,process_id",
            await GetConstraintColumnsAsync(ProcessForeignKey, "FOREIGN KEY"));
        Assert.Equal(
            "organization_id,created_by_membership_id",
            await GetConstraintColumnsAsync(CreatorForeignKey, "FOREIGN KEY"));
        Assert.Equal(
            "organization_id,assignee_membership_id",
            await GetConstraintColumnsAsync(AssigneeForeignKey, "FOREIGN KEY"));
        Assert.Equal(
            "organization_id",
            await GetConstraintColumnsAsync(OrganizationForeignKey, "FOREIGN KEY"));
        Assert.Equal(
            ["RESTRICT", "RESTRICT", "RESTRICT", "RESTRICT"],
            await GetDeleteRulesAsync());

        string[] indexNames = await GetIndexNamesAsync();
        Assert.Equal(
            [
                "ak_legal_tasks_organization_id_id",
                "ix_legal_tasks_completed_organization_completed_at_id",
                "ix_legal_tasks_organization_id_created_by_membership_id",
                "ix_legal_tasks_pending_due_date_organization_id_id",
                "ix_legal_tasks_pending_org_assignee_due_date_created_at_id",
                "ix_legal_tasks_pending_org_process_due_date_created_at_id",
                "ix_legal_tasks_pending_organization_due_date_created_at_id",
                "pk_legal_tasks"
            ],
            indexNames);
        await AssertIndexDefinitionAsync(
            "ix_legal_tasks_pending_organization_due_date_created_at_id",
            "(organization_id, due_date, created_at DESC, id)",
            "WHERE (completed_at IS NULL)");
        await AssertIndexDefinitionAsync(
            "ix_legal_tasks_completed_organization_completed_at_id",
            "(organization_id, completed_at DESC, id)",
            "WHERE (completed_at IS NOT NULL)");
        await AssertIndexDefinitionAsync(
            "ix_legal_tasks_pending_org_process_due_date_created_at_id",
            "(organization_id, process_id, due_date, created_at DESC, id)",
            "WHERE (completed_at IS NULL)");
        await AssertIndexDefinitionAsync(
            "ix_legal_tasks_pending_org_assignee_due_date_created_at_id",
            "(organization_id, assignee_membership_id, due_date, created_at DESC, id)",
            "WHERE (completed_at IS NULL)");
        await AssertIndexDefinitionAsync(
            "ix_legal_tasks_pending_due_date_organization_id_id",
            "(due_date, organization_id, id)",
            "WHERE ((completed_at IS NULL) AND (due_date IS NOT NULL))");

        Assert.Equal(
            [
                "ck_legal_tasks_completion",
                "ck_legal_tasks_description_normalized",
                "ck_legal_tasks_title_normalized"
            ],
            await GetCheckConstraintNamesAsync());
    }

    public static TheoryData<DateOnly?> RoundTripDueDates =>
        new()
        {
            null,
            new DateOnly(2020, 1, 1),
            new DateOnly(2030, 12, 31),
            new DateOnly(2028, 2, 29)
        };

    private async Task AssertForeignKeyViolationAsync(
        LegalTask legalTask,
        string expectedConstraintName)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.LegalTasks.Add(legalTask);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            expectedConstraintName);
    }

    private async Task AssertMembershipDeleteRestrictedAsync(
        Guid membershipId,
        string expectedConstraintName)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.OrganizationMemberships
                .Where(membership => membership.Id == membershipId)
                .ExecuteDeleteAsync());

        Assert.Equal(PostgresErrorCodes.RestrictViolation, exception.SqlState);
        Assert.Equal(expectedConstraintName, exception.ConstraintName);
        Assert.Equal(1, await dbContext.LegalTasks.CountAsync());
    }

    private async Task<PostgresException> AssertInvalidDirectInsertAsync(
        TenantGraph graph,
        string title,
        string? description,
        DateTimeOffset? completedAt)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        return await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO legal_tasks
                    (id, organization_id, title, description, due_date, process_id,
                     assignee_membership_id, created_by_membership_id, created_at,
                     completed_at)
                VALUES
                    ({Guid.NewGuid()}, {graph.Organization.Id}, {title}, {description},
                     NULL, NULL, NULL, {graph.CreatorMembership.Id}, {CreatedAt},
                     {completedAt})
                """));
    }

    private async Task AssertIndexDefinitionAsync(
        string indexName,
        string expectedColumns,
        string expectedPredicate)
    {
        string? indexDefinition = await GetIndexDefinitionAsync(indexName);

        Assert.NotNull(indexDefinition);
        Assert.Contains(expectedColumns, indexDefinition);
        Assert.Contains(expectedPredicate, indexDefinition);
    }

    private async Task SeedGraphAndTaskAsync(
        TenantGraph graph,
        LegalTask legalTask)
    {
        await SeedAsync(GetGraphEntities(graph).Append(legalTask).ToArray());
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static object[] GetGraphEntities(TenantGraph graph)
    {
        return
        [
            graph.Organization,
            graph.CreatorUser,
            graph.CreatorMembership,
            graph.AssigneeUser,
            graph.AssigneeMembership,
            graph.Client,
            graph.LegalProcess
        ];
    }

    private static LegalTask CreateFullLegalTask(TenantGraph graph)
    {
        return new LegalTask(
            graph.Organization.Id,
            "Prepare Defense",
            "Review documents",
            new DateOnly(2028, 2, 29),
            graph.LegalProcess.Id,
            graph.AssigneeMembership.Id,
            graph.CreatorMembership.Id,
            CreatedAt);
    }

    private static TenantGraph CreateTenantGraph(string name, string slug)
    {
        var organization = new Organization(name, slug, CreatedAt);
        var creatorUser = new User(
            $"{name} Creator",
            $"{slug}.creator@example.test",
            CreatedAt);
        var creatorMembership = new OrganizationMembership(
            organization.Id,
            creatorUser.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        var assigneeUser = new User(
            $"{name} Assignee",
            $"{slug}.assignee@example.test",
            CreatedAt);
        var assigneeMembership = new OrganizationMembership(
            organization.Id,
            assigneeUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        var client = new Client(
            organization.Id,
            $"{name} Client",
            CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            $"{name} Process",
            CreatedAt);

        return new TenantGraph(
            organization,
            creatorUser,
            creatorMembership,
            assigneeUser,
            assigneeMembership,
            client,
            legalProcess);
    }

    private static int PropertyOrder(string propertyName)
    {
        return Array.IndexOf(
            [
                nameof(LegalTask.Id),
                nameof(LegalTask.OrganizationId),
                nameof(LegalTask.Title),
                nameof(LegalTask.Description),
                nameof(LegalTask.DueDate),
                nameof(LegalTask.ProcessId),
                nameof(LegalTask.AssigneeMembershipId),
                nameof(LegalTask.CreatedByMembershipId),
                nameof(LegalTask.CreatedAt),
                nameof(LegalTask.CompletedAt)
            ],
            propertyName);
    }

    private static void AssertIndex(
        IEntityType entityType,
        string databaseName,
        string[] properties,
        bool[]? descending,
        string? filter)
    {
        IIndex index = Assert.Single(
            entityType.GetIndexes(),
            candidate => candidate.GetDatabaseName() == databaseName);
        Assert.Equal(
            properties,
            index.Properties.Select(property => property.Name).ToArray());
        Assert.Equal(descending, index.IsDescending);
        Assert.Equal(filter, index.GetFilter());
    }

    private static void AssertForeignKey(
        IEntityType entityType,
        string constraintName,
        Type principalType,
        string[] properties,
        string[] principalProperties)
    {
        IForeignKey foreignKey = Assert.Single(
            entityType.GetForeignKeys(),
            candidate => candidate.GetConstraintName() == constraintName);
        Assert.Equal(principalType, foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.Equal(
            properties,
            foreignKey.Properties.Select(property => property.Name).ToArray());
        Assert.Equal(
            principalProperties,
            foreignKey.PrincipalKey.Properties
                .Select(property => property.Name)
                .ToArray());
    }

    private async Task<string?> GetConstraintColumnsAsync(
        string constraintName,
        string constraintType)
    {
        const string Query =
            """
            SELECT string_agg(kcu.column_name, ',' ORDER BY kcu.ordinal_position)
            FROM information_schema.table_constraints AS tc
            INNER JOIN information_schema.key_column_usage AS kcu
                ON kcu.constraint_schema = tc.constraint_schema
                AND kcu.constraint_name = tc.constraint_name
            WHERE tc.constraint_schema = 'public'
              AND tc.table_name = 'legal_tasks'
              AND tc.constraint_name = @constraintName
              AND tc.constraint_type = @constraintType
            """;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        command.Parameters.AddWithValue("constraintName", constraintName);
        command.Parameters.AddWithValue("constraintType", constraintType);
        object? result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    private async Task<string[]> GetDeleteRulesAsync()
    {
        const string Query =
            """
            SELECT delete_rule
            FROM information_schema.referential_constraints
            WHERE constraint_schema = 'public'
              AND constraint_name LIKE 'fk_legal_tasks_%'
            ORDER BY constraint_name
            """;

        return await ExecuteStringArrayAsync(Query);
    }

    private async Task<string[]> GetIndexNamesAsync()
    {
        const string Query =
            """
            SELECT indexname
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'legal_tasks'
            ORDER BY indexname
            """;

        return await ExecuteStringArrayAsync(Query);
    }

    private async Task<string?> GetIndexDefinitionAsync(string indexName)
    {
        const string Query =
            """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'legal_tasks'
              AND indexname = @value
            """;

        return await ExecuteScalarStringAsync(Query, indexName);
    }

    private async Task<string?> GetColumnTypeAsync(string columnName)
    {
        const string Query =
            """
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'legal_tasks'
              AND column_name = @value
            """;

        return await ExecuteScalarStringAsync(Query, columnName);
    }

    private async Task<string?> GetTableColumnsAsync()
    {
        const string Query =
            """
            SELECT string_agg(column_name, ',' ORDER BY ordinal_position)
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'legal_tasks'
            """;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        object? result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    private async Task<string[]> GetCheckConstraintNamesAsync()
    {
        const string Query =
            """
            SELECT constraint_name
            FROM information_schema.table_constraints
            WHERE constraint_schema = 'public'
              AND table_name = 'legal_tasks'
              AND constraint_type = 'CHECK'
              AND constraint_name LIKE 'ck_legal_tasks_%'
            ORDER BY constraint_name
            """;

        return await ExecuteStringArrayAsync(Query);
    }

    private async Task<string?> ExecuteScalarStringAsync(
        string query,
        string value)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("value", value);
        object? result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    private async Task<string[]> ExecuteStringArrayAsync(string query)
    {
        var values = new List<string>();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(query, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values.ToArray();
    }

    private static void AssertPostgresException(
        DbUpdateException exception,
        string expectedSqlState,
        string expectedConstraintName)
    {
        PostgresException postgresException =
            Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(expectedSqlState, postgresException.SqlState);
        Assert.Equal(expectedConstraintName, postgresException.ConstraintName);
    }

    private sealed record TenantGraph(
        Organization Organization,
        User CreatorUser,
        OrganizationMembership CreatorMembership,
        User AssigneeUser,
        OrganizationMembership AssigneeMembership,
        Client Client,
        LegalProcess LegalProcess);
}
