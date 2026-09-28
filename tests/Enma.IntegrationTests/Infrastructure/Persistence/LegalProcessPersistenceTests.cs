using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalProcessPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        12,
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
    public async Task SaveChangesAsync_WithSameTenantClient_PersistsLegalProcess()
    {
        Organization organization = CreateOrganization("Alpha", "alpha");
        var client = new Client(organization.Id, "Alpha Client", CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            "Contract Review",
            CreatedAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(organization, client, legalProcess);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        LegalProcess persistedLegalProcess =
            await dbContext.LegalProcesses.SingleAsync();

        Assert.Equal(legalProcess.Id, persistedLegalProcess.Id);
        Assert.Equal(organization.Id, persistedLegalProcess.OrganizationId);
        Assert.Equal(client.Id, persistedLegalProcess.ClientId);
        Assert.Equal("Contract Review", persistedLegalProcess.Title);
        Assert.Equal(CreatedAt, persistedLegalProcess.CreatedAt);
    }

    [Fact]
    public async Task SaveChangesAsync_WithCrossTenantClient_EnforcesCompositeForeignKey()
    {
        Organization organizationA = CreateOrganization("Alpha", "alpha");
        Organization organizationB = CreateOrganization("Beta", "beta");
        var clientB = new Client(
            organizationB.Id,
            "Beta Client",
            CreatedAt);
        await SeedAsync(organizationA, organizationB, clientB);
        var legalProcess = new LegalProcess(
            organizationA.Id,
            clientB.Id,
            "Cross-tenant Process",
            CreatedAt);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.LegalProcesses.Add(legalProcess);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            "fk_legal_processes_clients_organization_id_client_id");
    }

    [Fact]
    public async Task SaveChangesAsync_WhenDeletingReferencedClient_RestrictsDelete()
    {
        Organization organization = CreateOrganization("Alpha", "alpha");
        var client = new Client(organization.Id, "Alpha Client", CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            "Contract Review",
            CreatedAt);
        await SeedAsync(organization, client, legalProcess);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Client persistedClient = await dbContext.Clients.SingleAsync();
        dbContext.Clients.Remove(persistedClient);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.RestrictViolation,
            "fk_legal_processes_clients_organization_id_client_id");
    }

    [Fact]
    public async Task SaveChangesAsync_WhenDeactivatingClient_PreservesLegalProcess()
    {
        Organization organization = CreateOrganization("Alpha", "alpha");
        var client = new Client(organization.Id, "Alpha Client", CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            "Contract Review",
            CreatedAt);
        await SeedAsync(organization, client, legalProcess);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Client persistedClient = await dbContext.Clients.SingleAsync();

        persistedClient.Deactivate();
        await dbContext.SaveChangesAsync();

        Assert.False(persistedClient.IsActive);
        Assert.True(await dbContext.LegalProcesses.AnyAsync(
            candidate => candidate.Id == legalProcess.Id));
    }

    [Fact]
    public async Task SaveChangesAsync_WithOperationalFields_PersistsValues()
    {
        Organization organization = CreateOrganization("Alpha", "alpha");
        var user = new User("Responsible User", "responsible@example.test", CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt);
        var client = new Client(organization.Id, "Alpha Client", CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            "Contract Review",
            CreatedAt,
            "0001234-56.2026.8.19.0001",
            "Regional Court",
            membership.Id);
        legalProcess.ChangeStatus(LegalProcessStatus.Suspended);

        await SeedAsync(organization, user, membership, client, legalProcess);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        LegalProcess persisted = await dbContext.LegalProcesses.SingleAsync();

        Assert.Equal("0001234-56.2026.8.19.0001", persisted.ProcessNumber);
        Assert.Equal("00012345620268190001", persisted.NormalizedProcessNumber);
        Assert.Equal(LegalProcessStatus.Suspended, persisted.Status);
        Assert.Equal("Regional Court", persisted.CourtOrAuthority);
        Assert.Equal(membership.Id, persisted.ResponsibleMembershipId);
    }

    [Fact]
    public async Task SaveChangesAsync_WithDuplicateNormalizedNumberInTenant_Conflicts()
    {
        Organization organization = CreateOrganization("Alpha", "alpha");
        var client = new Client(organization.Id, "Alpha Client", CreatedAt);
        var formatted = new LegalProcess(
            organization.Id,
            client.Id,
            "Formatted",
            CreatedAt,
            "0001234-56.2026.8.19.0001");
        var digitsOnly = new LegalProcess(
            organization.Id,
            client.Id,
            "Digits",
            CreatedAt,
            "00012345620268190001");
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(organization, client, formatted, digitsOnly);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.UniqueViolation,
            "ux_legal_processes_organization_id_normalized_process_number");
    }

    [Fact]
    public async Task SaveChangesAsync_WithSameNormalizedNumberAcrossTenants_PersistsBoth()
    {
        Organization organizationA = CreateOrganization("Alpha", "alpha");
        Organization organizationB = CreateOrganization("Beta", "beta");
        var clientA = new Client(organizationA.Id, "Alpha Client", CreatedAt);
        var clientB = new Client(organizationB.Id, "Beta Client", CreatedAt);
        var processA = new LegalProcess(
            organizationA.Id,
            clientA.Id,
            "Alpha Process",
            CreatedAt,
            "0001234-56.2026.8.19.0001");
        var processB = new LegalProcess(
            organizationB.Id,
            clientB.Id,
            "Beta Process",
            CreatedAt,
            "00012345620268190001");

        await SeedAsync(
            organizationA,
            organizationB,
            clientA,
            clientB,
            processA,
            processB);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(2, await dbContext.LegalProcesses.CountAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_WithMultipleNullProcessNumbers_PersistsAll()
    {
        Organization organization = CreateOrganization("Alpha", "alpha");
        var client = new Client(organization.Id, "Alpha Client", CreatedAt);
        var first = new LegalProcess(
            organization.Id,
            client.Id,
            "First",
            CreatedAt);
        var second = new LegalProcess(
            organization.Id,
            client.Id,
            "Second",
            CreatedAt);

        await SeedAsync(organization, client, first, second);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(2, await dbContext.LegalProcesses.CountAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_WithCrossTenantResponsibleMembership_EnforcesCompositeForeignKey()
    {
        Organization organizationA = CreateOrganization("Alpha", "alpha");
        Organization organizationB = CreateOrganization("Beta", "beta");
        var userB = new User("Beta User", "beta@example.test", CreatedAt);
        var membershipB = new OrganizationMembership(
            organizationB.Id,
            userB.Id,
            OrganizationRole.Member,
            CreatedAt);
        var clientA = new Client(organizationA.Id, "Alpha Client", CreatedAt);
        var legalProcess = new LegalProcess(
            organizationA.Id,
            clientA.Id,
            "Cross-tenant Responsibility",
            CreatedAt,
            responsibleMembershipId: membershipB.Id);
        await SeedAsync(
            organizationA,
            organizationB,
            userB,
            membershipB,
            clientA);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.LegalProcesses.Add(legalProcess);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            "fk_legal_processes_memberships_org_responsible_membership_id");
    }

    [Fact]
    public async Task SaveChangesAsync_WhenDeletingResponsibleMembership_RestrictsDelete()
    {
        Organization organization = CreateOrganization("Alpha", "alpha");
        var user = new User("Responsible User", "responsible@example.test", CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt);
        var client = new Client(organization.Id, "Alpha Client", CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            "Contract Review",
            CreatedAt,
            responsibleMembershipId: membership.Id);
        await SeedAsync(organization, user, membership, client, legalProcess);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembership persistedMembership =
            await dbContext.OrganizationMemberships.SingleAsync();
        dbContext.OrganizationMemberships.Remove(persistedMembership);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.RestrictViolation,
            "fk_legal_processes_memberships_org_responsible_membership_id");
    }

    [Theory]
    [InlineData(
        "UPDATE legal_processes SET status = 0",
        "ck_legal_processes_status")]
    [InlineData(
        "UPDATE legal_processes SET process_number = 'ABC', normalized_process_number = NULL",
        "ck_legal_processes_process_number_pair")]
    [InlineData(
        "UPDATE legal_processes SET process_number = ' ABC ', normalized_process_number = 'ABC'",
        "ck_legal_processes_process_number_normalized")]
    [InlineData(
        "UPDATE legal_processes SET court_or_authority = ' Court '",
        "ck_legal_processes_court_or_authority_normalized")]
    public async Task PostgreSqlSchema_WithInvalidOperationalValue_EnforcesCheck(
        string sql,
        string constraintName)
    {
        Organization organization = CreateOrganization("Alpha", "alpha");
        var client = new Client(organization.Id, "Alpha Client", CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            "Contract Review",
            CreatedAt);
        await SeedAsync(organization, client, legalProcess);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteNonQueryAsync(sql));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(constraintName, exception.ConstraintName);
    }

    [Fact]
    public void LegalProcessModel_WithTenantOwnership_HasExpectedRelationshipsAndIndex()
    {
        using EnmaDbContext dbContext = fixture.CreateDbContext();
        IEntityType? entityType = dbContext.Model.FindEntityType(
            typeof(LegalProcess));

        Assert.NotNull(entityType);
        Assert.Equal("legal_processes", entityType.GetTableName());
        Assert.False(
            entityType.FindProperty(nameof(LegalProcess.OrganizationId))!
                .IsNullable);
        Assert.False(
            entityType.FindProperty(nameof(LegalProcess.ClientId))!.IsNullable);
        Assert.False(
            entityType.FindProperty(nameof(LegalProcess.Title))!.IsNullable);
        Assert.Equal(
            150,
            entityType.FindProperty(nameof(LegalProcess.Title))!.GetMaxLength());
        Assert.True(
            entityType.FindProperty(nameof(LegalProcess.ProcessNumber))!
                .IsNullable);
        Assert.Equal(
            100,
            entityType.FindProperty(nameof(LegalProcess.ProcessNumber))!
                .GetMaxLength());
        Assert.True(
            entityType.FindProperty(nameof(LegalProcess.NormalizedProcessNumber))!
                .IsNullable);
        Assert.Equal(
            100,
            entityType.FindProperty(nameof(LegalProcess.NormalizedProcessNumber))!
                .GetMaxLength());
        Assert.False(
            entityType.FindProperty(nameof(LegalProcess.Status))!.IsNullable);
        Assert.Equal(
            (int)LegalProcessStatus.InProgress,
            Convert.ToInt32(
                entityType.FindProperty(nameof(LegalProcess.Status))!
                    .GetDefaultValue()));
        Assert.True(
            entityType.FindProperty(nameof(LegalProcess.CourtOrAuthority))!
                .IsNullable);
        Assert.Equal(
            200,
            entityType.FindProperty(nameof(LegalProcess.CourtOrAuthority))!
                .GetMaxLength());
        Assert.True(
            entityType.FindProperty(nameof(LegalProcess.ResponsibleMembershipId))!
                .IsNullable);

        IKey alternateKey = Assert.Single(
            entityType.GetKeys(),
            key => !key.IsPrimaryKey());
        Assert.Equal(
            "ak_legal_processes_organization_id_id",
            alternateKey.GetName());
        Assert.Equal(
            [nameof(LegalProcess.OrganizationId), nameof(LegalProcess.Id)],
            alternateKey.Properties.Select(property => property.Name).ToArray());

        AssertIndex(
            entityType,
            "ix_legal_processes_organization_id_client_id",
            [nameof(LegalProcess.OrganizationId), nameof(LegalProcess.ClientId)],
            isUnique: false,
            filter: null);
        AssertIndex(
            entityType,
            "ux_legal_processes_organization_id_normalized_process_number",
            [
                nameof(LegalProcess.OrganizationId),
                nameof(LegalProcess.NormalizedProcessNumber)
            ],
            isUnique: true,
            filter: "normalized_process_number IS NOT NULL");
        AssertIndex(
            entityType,
            "ix_legal_processes_organization_id_status",
            [nameof(LegalProcess.OrganizationId), nameof(LegalProcess.Status)],
            isUnique: false,
            filter: null);
        AssertIndex(
            entityType,
            "ix_legal_processes_organization_id_responsible_membership_id",
            [
                nameof(LegalProcess.OrganizationId),
                nameof(LegalProcess.ResponsibleMembershipId)
            ],
            isUnique: false,
            filter: null);

        IForeignKey organizationForeignKey = Assert.Single(
            entityType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType ==
                typeof(Organization));
        Assert.Equal(DeleteBehavior.Restrict, organizationForeignKey.DeleteBehavior);
        Assert.Equal(
            [nameof(LegalProcess.OrganizationId)],
            organizationForeignKey.Properties
                .Select(property => property.Name)
                .ToArray());

        IForeignKey clientForeignKey = Assert.Single(
            entityType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Client));
        Assert.Equal(DeleteBehavior.Restrict, clientForeignKey.DeleteBehavior);
        Assert.Equal(
            [nameof(LegalProcess.OrganizationId), nameof(LegalProcess.ClientId)],
            clientForeignKey.Properties
                .Select(property => property.Name)
                .ToArray());
        Assert.Equal(
            [nameof(Client.OrganizationId), nameof(Client.Id)],
            clientForeignKey.PrincipalKey.Properties
                .Select(property => property.Name)
                .ToArray());

        IForeignKey membershipForeignKey = Assert.Single(
            entityType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType ==
                typeof(OrganizationMembership));
        Assert.Equal(DeleteBehavior.Restrict, membershipForeignKey.DeleteBehavior);
        Assert.Equal(
            [
                nameof(LegalProcess.OrganizationId),
                nameof(LegalProcess.ResponsibleMembershipId)
            ],
            membershipForeignKey.Properties
                .Select(property => property.Name)
                .ToArray());
        Assert.Equal(
            [
                nameof(OrganizationMembership.OrganizationId),
                nameof(OrganizationMembership.Id)
            ],
            membershipForeignKey.PrincipalKey.Properties
                .Select(property => property.Name)
                .ToArray());
    }

    [Fact]
    public async Task PostgreSqlSchema_WithLegalProcesses_HasExpectedConstraintsAndIndexes()
    {
        Assert.Equal(
            "RESTRICT",
            await GetDeleteRuleAsync(
                "fk_legal_processes_organizations_organization_id"));
        Assert.Equal(
            "RESTRICT",
            await GetDeleteRuleAsync(
                "fk_legal_processes_clients_organization_id_client_id"));
        Assert.Equal(
            "RESTRICT",
            await GetDeleteRuleAsync(
                "fk_legal_processes_memberships_org_responsible_membership_id"));
        Assert.Equal(
            "organization_id,id",
            await GetConstraintColumnsAsync(
                "clients",
                "ak_clients_organization_id_id",
                "UNIQUE"));
        Assert.Equal(
            "organization_id,id",
            await GetConstraintColumnsAsync(
                "legal_processes",
                "ak_legal_processes_organization_id_id",
                "UNIQUE"));

        string? legalProcessIndex = await GetIndexDefinitionAsync(
            "legal_processes",
            "ix_legal_processes_organization_id_client_id");
        Assert.NotNull(legalProcessIndex);
        Assert.Contains("(organization_id, client_id)", legalProcessIndex);
        string? normalizedNumberIndex = await GetIndexDefinitionAsync(
            "legal_processes",
            "ux_legal_processes_organization_id_normalized_process_number");
        Assert.NotNull(normalizedNumberIndex);
        Assert.Contains(
            "UNIQUE INDEX",
            normalizedNumberIndex,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "(organization_id, normalized_process_number)",
            normalizedNumberIndex);
        Assert.Contains(
            "WHERE (normalized_process_number IS NOT NULL)",
            normalizedNumberIndex);
        Assert.NotNull(await GetIndexDefinitionAsync(
            "legal_processes",
            "ix_legal_processes_organization_id_status"));
        Assert.NotNull(await GetIndexDefinitionAsync(
            "legal_processes",
            "ix_legal_processes_organization_id_responsible_membership_id"));
        string? clientAlternateKeyIndex = await GetIndexDefinitionAsync(
            "clients",
            "ak_clients_organization_id_id");
        Assert.NotNull(clientAlternateKeyIndex);
        Assert.Contains("(organization_id, id)", clientAlternateKeyIndex);
        string? legalProcessAlternateKeyIndex = await GetIndexDefinitionAsync(
            "legal_processes",
            "ak_legal_processes_organization_id_id");
        Assert.NotNull(legalProcessAlternateKeyIndex);
        Assert.Contains("(organization_id, id)", legalProcessAlternateKeyIndex);
        Assert.Null(await GetIndexDefinitionAsync(
            "clients",
            "ix_clients_organization_id"));
        Assert.Equal(
            [
                "ck_legal_processes_court_or_authority_normalized",
                "ck_legal_processes_process_number_normalized",
                "ck_legal_processes_process_number_pair",
                "ck_legal_processes_status"
            ],
            await GetCheckConstraintNamesAsync());
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
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

    private async Task<string?> GetConstraintColumnsAsync(
        string tableName,
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
              AND tc.table_name = @tableName
              AND tc.constraint_name = @constraintName
              AND tc.constraint_type = @constraintType
            """;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        command.Parameters.AddWithValue("tableName", tableName);
        command.Parameters.AddWithValue("constraintName", constraintName);
        command.Parameters.AddWithValue("constraintType", constraintType);
        object? result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    private async Task<string?> GetIndexDefinitionAsync(
        string tableName,
        string indexName)
    {
        const string Query =
            """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = @tableName
              AND indexname = @indexName
            """;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        command.Parameters.AddWithValue("tableName", tableName);
        command.Parameters.AddWithValue("indexName", indexName);
        object? result = await command.ExecuteScalarAsync();
        return result is null ? null : (string)result;
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
        return result is null ? null : (string)result;
    }

    private async Task ExecuteNonQueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string[]> GetCheckConstraintNamesAsync()
    {
        const string Query =
            """
            SELECT constraint_name
            FROM information_schema.table_constraints
            WHERE constraint_schema = 'public'
              AND table_name = 'legal_processes'
              AND constraint_type = 'CHECK'
              AND constraint_name LIKE 'ck_legal_processes_%'
            ORDER BY constraint_name
            """;

        var values = new List<string>();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values.ToArray();
    }

    private static void AssertIndex(
        IEntityType entityType,
        string databaseName,
        string[] properties,
        bool isUnique,
        string? filter)
    {
        IIndex index = Assert.Single(
            entityType.GetIndexes(),
            candidate => candidate.GetDatabaseName() == databaseName);
        Assert.Equal(
            properties,
            index.Properties.Select(property => property.Name).ToArray());
        Assert.Equal(isUnique, index.IsUnique);
        Assert.Equal(filter, index.GetFilter());
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
