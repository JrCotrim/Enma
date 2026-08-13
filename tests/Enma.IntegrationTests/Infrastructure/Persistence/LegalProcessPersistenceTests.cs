using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
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

        IKey alternateKey = Assert.Single(
            entityType.GetKeys(),
            key => !key.IsPrimaryKey());
        Assert.Equal(
            "ak_legal_processes_organization_id_id",
            alternateKey.GetName());
        Assert.Equal(
            [nameof(LegalProcess.OrganizationId), nameof(LegalProcess.Id)],
            alternateKey.Properties.Select(property => property.Name).ToArray());

        IIndex index = Assert.Single(entityType.GetIndexes());
        Assert.Equal(
            "ix_legal_processes_organization_id_client_id",
            index.GetDatabaseName());
        Assert.Equal(
            [nameof(LegalProcess.OrganizationId), nameof(LegalProcess.ClientId)],
            index.Properties.Select(property => property.Name).ToArray());

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
