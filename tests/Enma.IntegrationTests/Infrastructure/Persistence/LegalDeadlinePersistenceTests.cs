using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDeadlinePersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateOnly DueDate = new(2026, 11, 1);

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        13,
        17,
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
    public async Task SaveChangesAsync_WithSameTenantProcess_PersistsPendingLegalDeadline()
    {
        (Organization organization, Client client, LegalProcess legalProcess) =
            CreateProcessGraph("Alpha", "alpha");
        var legalDeadline = new LegalDeadline(
            organization.Id,
            legalProcess.Id,
            "  File Appellate Brief  ",
            DueDate,
            CreatedAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(organization, client, legalProcess, legalDeadline);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        LegalDeadline persistedLegalDeadline =
            await dbContext.LegalDeadlines.SingleAsync();

        Assert.Equal(legalDeadline.Id, persistedLegalDeadline.Id);
        Assert.Equal(organization.Id, persistedLegalDeadline.OrganizationId);
        Assert.Equal(legalProcess.Id, persistedLegalDeadline.ProcessId);
        Assert.Equal("File Appellate Brief", persistedLegalDeadline.Title);
        Assert.Equal(DueDate, persistedLegalDeadline.DueDate);
        Assert.Equal(CreatedAt, persistedLegalDeadline.CreatedAt);
        Assert.Null(persistedLegalDeadline.CompletedAt);
    }

    [Fact]
    public async Task SaveChangesAsync_WithCrossTenantProcess_EnforcesCompositeForeignKey()
    {
        Organization organizationA = CreateOrganization("Alpha", "alpha");
        (Organization organizationB, Client clientB, LegalProcess legalProcessB) =
            CreateProcessGraph("Beta", "beta");
        await SeedAsync(organizationA, organizationB, clientB, legalProcessB);
        var legalDeadline = new LegalDeadline(
            organizationA.Id,
            legalProcessB.Id,
            "Cross-tenant Deadline",
            DueDate,
            CreatedAt);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.LegalDeadlines.Add(legalDeadline);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            "fk_legal_deadlines_legal_processes_organization_id_process_id");
    }

    [Fact]
    public async Task SaveChangesAsync_WithMissingProcess_EnforcesCompositeForeignKey()
    {
        Organization organization = CreateOrganization("Alpha", "alpha");
        await SeedAsync(organization);
        var legalDeadline = new LegalDeadline(
            organization.Id,
            Guid.NewGuid(),
            "Missing Process Deadline",
            DueDate,
            CreatedAt);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.LegalDeadlines.Add(legalDeadline);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            "fk_legal_deadlines_legal_processes_organization_id_process_id");
    }

    [Fact]
    public async Task SaveChangesAsync_WithMissingOrganization_RejectsLegalDeadline()
    {
        var legalDeadline = new LegalDeadline(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Missing Organization Deadline",
            DueDate,
            CreatedAt);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.LegalDeadlines.Add(legalDeadline);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        PostgresException postgresException =
            Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgresException.SqlState);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenDeletingReferencedProcess_RestrictsDelete()
    {
        (Organization organization, Client client, LegalProcess legalProcess) =
            CreateProcessGraph("Alpha", "alpha");
        var legalDeadline = new LegalDeadline(
            organization.Id,
            legalProcess.Id,
            "File Appellate Brief",
            DueDate,
            CreatedAt);
        await SeedAsync(organization, client, legalProcess, legalDeadline);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        LegalProcess persistedLegalProcess =
            await dbContext.LegalProcesses.SingleAsync();
        dbContext.LegalProcesses.Remove(persistedLegalProcess);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.RestrictViolation,
            "fk_legal_deadlines_legal_processes_organization_id_process_id");
    }

    [Fact]
    public async Task SaveChangesAsync_AcrossLifecycle_PersistsCompletedAtState()
    {
        (Organization organization, Client client, LegalProcess legalProcess) =
            CreateProcessGraph("Alpha", "alpha");
        var legalDeadline = new LegalDeadline(
            organization.Id,
            legalProcess.Id,
            "File Appellate Brief",
            DueDate,
            CreatedAt);
        await SeedAsync(organization, client, legalProcess, legalDeadline);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        LegalDeadline persistedLegalDeadline =
            await dbContext.LegalDeadlines.SingleAsync();
        DateTimeOffset completedAt = CreatedAt.AddDays(2);

        Assert.Null(persistedLegalDeadline.CompletedAt);

        persistedLegalDeadline.Complete(completedAt);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        persistedLegalDeadline = await dbContext.LegalDeadlines.SingleAsync();
        Assert.Equal(completedAt, persistedLegalDeadline.CompletedAt);

        persistedLegalDeadline.Reopen();
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        persistedLegalDeadline = await dbContext.LegalDeadlines.SingleAsync();
        Assert.Null(persistedLegalDeadline.CompletedAt);
    }

    [Fact]
    public async Task DatabaseInsert_WithCompletionBeforeCreation_EnforcesCheckConstraint()
    {
        (Organization organization, Client client, LegalProcess legalProcess) =
            CreateProcessGraph("Alpha", "alpha");
        await SeedAsync(organization, client, legalProcess);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        DateTimeOffset invalidCompletedAt = CreatedAt.AddTicks(-1);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO legal_deadlines
                    (id, organization_id, process_id, title, due_date, created_at, completed_at)
                VALUES
                    ({Guid.NewGuid()}, {organization.Id}, {legalProcess.Id},
                     {"File Appellate Brief"}, {DueDate}, {CreatedAt}, {invalidCompletedAt})
                """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_legal_deadlines_completion", exception.ConstraintName);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("  Unnormalized Title  ")]
    public async Task DatabaseInsert_WithUnnormalizedTitle_EnforcesCheckConstraint(
        string title)
    {
        (Organization organization, Client client, LegalProcess legalProcess) =
            CreateProcessGraph("Alpha", "alpha");
        await SeedAsync(organization, client, legalProcess);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO legal_deadlines
                    (id, organization_id, process_id, title, due_date, created_at, completed_at)
                VALUES
                    ({Guid.NewGuid()}, {organization.Id}, {legalProcess.Id},
                     {title}, {DueDate}, {CreatedAt}, NULL)
                """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(
            "ck_legal_deadlines_title_normalized",
            exception.ConstraintName);
    }

    [Fact]
    public void LegalDeadlineModel_WithLockedShape_HasExpectedRelationalMetadata()
    {
        using EnmaDbContext dbContext = fixture.CreateDbContext();
        IEntityType? entityType = dbContext.Model.FindEntityType(
            typeof(LegalDeadline));

        Assert.NotNull(entityType);
        Assert.Equal("legal_deadlines", entityType.GetTableName());
        Assert.Equal(
            [
                nameof(LegalDeadline.Id),
                nameof(LegalDeadline.OrganizationId),
                nameof(LegalDeadline.ProcessId),
                nameof(LegalDeadline.Title),
                nameof(LegalDeadline.DueDate),
                nameof(LegalDeadline.CreatedAt),
                nameof(LegalDeadline.CompletedAt)
            ],
            entityType.GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => Array.IndexOf(
                    [
                        nameof(LegalDeadline.Id),
                        nameof(LegalDeadline.OrganizationId),
                        nameof(LegalDeadline.ProcessId),
                        nameof(LegalDeadline.Title),
                        nameof(LegalDeadline.DueDate),
                        nameof(LegalDeadline.CreatedAt),
                        nameof(LegalDeadline.CompletedAt)
                    ],
                    name))
                .ToArray());
        Assert.Null(entityType.FindProperty("ClientId"));
        Assert.Null(entityType.FindProperty("Status"));
        Assert.Null(entityType.FindProperty("IsCompleted"));
        Assert.Null(entityType.FindProperty("IsOverdue"));
        Assert.Equal(
            "date",
            entityType.FindProperty(nameof(LegalDeadline.DueDate))!.GetColumnType());
        Assert.Equal(
            "timestamp with time zone",
            entityType.FindProperty(nameof(LegalDeadline.CreatedAt))!.GetColumnType());
        Assert.Equal(
            "timestamp with time zone",
            entityType.FindProperty(nameof(LegalDeadline.CompletedAt))!.GetColumnType());
        Assert.True(
            entityType.FindProperty(nameof(LegalDeadline.CompletedAt))!.IsNullable);
        Assert.Equal(
            150,
            entityType.FindProperty(nameof(LegalDeadline.Title))!.GetMaxLength());
        Assert.Empty(entityType.GetNavigations());

        IIndex[] indexes = entityType.GetIndexes().ToArray();
        Assert.Equal(2, indexes.Length);
        Assert.Contains(
            indexes,
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual(
                    [
                        nameof(LegalDeadline.OrganizationId),
                        nameof(LegalDeadline.DueDate),
                        nameof(LegalDeadline.Id)
                    ]));
        Assert.Contains(
            indexes,
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual(
                    [
                        nameof(LegalDeadline.OrganizationId),
                        nameof(LegalDeadline.ProcessId),
                        nameof(LegalDeadline.DueDate),
                        nameof(LegalDeadline.Id)
                    ]));

        IForeignKey organizationForeignKey = Assert.Single(
            entityType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType ==
                typeof(Organization));
        Assert.Equal(DeleteBehavior.Restrict, organizationForeignKey.DeleteBehavior);
        Assert.Equal(
            [nameof(LegalDeadline.OrganizationId)],
            organizationForeignKey.Properties.Select(property => property.Name).ToArray());

        IForeignKey processForeignKey = Assert.Single(
            entityType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType ==
                typeof(LegalProcess));
        Assert.Equal(DeleteBehavior.Restrict, processForeignKey.DeleteBehavior);
        Assert.Equal(
            [nameof(LegalDeadline.OrganizationId), nameof(LegalDeadline.ProcessId)],
            processForeignKey.Properties.Select(property => property.Name).ToArray());
        Assert.Equal(
            [nameof(LegalProcess.OrganizationId), nameof(LegalProcess.Id)],
            processForeignKey.PrincipalKey.Properties
                .Select(property => property.Name)
                .ToArray());
    }

    [Fact]
    public async Task PostgreSqlSchema_WithLegalDeadlines_HasExpectedConstraintsAndIndexes()
    {
        Assert.Equal(
            "organization_id,process_id",
            await GetConstraintColumnsAsync(
                "fk_legal_deadlines_legal_processes_organization_id_process_id",
                "FOREIGN KEY"));
        Assert.Equal(
            "organization_id",
            await GetConstraintColumnsAsync(
                "fk_legal_deadlines_organizations_organization_id",
                "FOREIGN KEY"));
        Assert.Equal(
            "RESTRICT",
            await GetDeleteRuleAsync(
                "fk_legal_deadlines_legal_processes_organization_id_process_id"));
        Assert.Equal(
            "RESTRICT",
            await GetDeleteRuleAsync(
                "fk_legal_deadlines_organizations_organization_id"));
        Assert.Equal(
            "organization_id,id",
            await GetConstraintColumnsAsync(
                "ak_legal_processes_organization_id_id",
                "UNIQUE"));

        string? organizationIndex = await GetIndexDefinitionAsync(
            "ix_legal_deadlines_organization_id_due_date_id");
        Assert.NotNull(organizationIndex);
        Assert.Contains("(organization_id, due_date, id)", organizationIndex);
        string? processIndex = await GetIndexDefinitionAsync(
            "ix_legal_deadlines_organization_id_process_id_due_date_id");
        Assert.NotNull(processIndex);
        Assert.Contains(
            "(organization_id, process_id, due_date, id)",
            processIndex);
        Assert.Null(await GetIndexDefinitionAsync(
            "ix_legal_deadlines_organization_id"));
        Assert.Null(await GetIndexDefinitionAsync(
            "ix_legal_deadlines_organization_id_process_id"));

        Assert.Equal("date", await GetColumnTypeAsync("due_date"));
        Assert.Equal(
            "timestamp with time zone",
            await GetColumnTypeAsync("created_at"));
        Assert.Equal(
            "timestamp with time zone",
            await GetColumnTypeAsync("completed_at"));
        Assert.Equal(
            "id,organization_id,process_id,title,due_date,created_at,completed_at",
            await GetTableColumnsAsync());
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
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

    private async Task<string?> GetDeleteRuleAsync(string constraintName)
    {
        const string Query =
            """
            SELECT delete_rule
            FROM information_schema.referential_constraints
            WHERE constraint_schema = 'public'
              AND constraint_name = @constraintName
            """;

        return await ExecuteScalarStringAsync(Query, constraintName);
    }

    private async Task<string?> GetIndexDefinitionAsync(string indexName)
    {
        const string Query =
            """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'legal_deadlines'
              AND indexname = @constraintName
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
              AND table_name = 'legal_deadlines'
              AND column_name = @constraintName
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
              AND table_name = 'legal_deadlines'
            """;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        object? result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    private async Task<string?> ExecuteScalarStringAsync(
        string query,
        string constraintName)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("constraintName", constraintName);
        object? result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    private static (Organization, Client, LegalProcess) CreateProcessGraph(
        string organizationName,
        string organizationSlug)
    {
        Organization organization = CreateOrganization(
            organizationName,
            organizationSlug);
        var client = new Client(
            organization.Id,
            $"{organizationName} Client",
            CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            $"{organizationName} Process",
            CreatedAt);

        return (organization, client, legalProcess);
    }

    private static Organization CreateOrganization(string name, string slug)
    {
        return new Organization(name, slug, CreatedAt);
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
}
