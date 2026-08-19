using Enma.Domain.Clients;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDocumentPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        19,
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
    public async Task SaveChangesAsync_WithGeneralDocument_PersistsMetadata()
    {
        SeedGraph graph = CreateGraph();
        LegalDocument document = CreateDocument(
            graph.Organization.Id,
            graph.Membership.Id);

        await SeedAsync(
            graph.Organization,
            graph.User,
            graph.Membership,
            document);

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();

        LegalDocument persisted =
            await dbContext.LegalDocuments
                .AsNoTracking()
                .SingleAsync();

        Assert.Equal(document.Id, persisted.Id);
        Assert.Equal(
            graph.Organization.Id,
            persisted.OrganizationId);
        Assert.Null(persisted.ClientId);
        Assert.Null(persisted.ProcessId);
        Assert.Equal(
            "contract.pdf",
            persisted.OriginalFileName);
        Assert.Equal(
            "0123456789abcdef0123456789abcdef",
            persisted.StoredObjectKey);
        Assert.Equal(
            "application/pdf",
            persisted.ContentType);
        Assert.Equal(1234, persisted.SizeBytes);
        Assert.Equal(
            CreateHash().ToArray(),
            persisted.ContentHashSha256.ToArray());
        Assert.Equal(
            graph.Membership.Id,
            persisted.UploadedByMembershipId);
        Assert.Equal(CreatedAt, persisted.CreatedAt);
    }

    [Fact]
    public async Task SaveChangesAsync_WithSameTenantClient_PersistsClientClassification()
    {
        SeedGraph graph = CreateGraph(
            includeClient: true);
        LegalDocument document = CreateDocument(
            graph.Organization.Id,
            graph.Membership.Id,
            clientId: graph.Client!.Id);

        await SeedAsync(
            graph.Organization,
            graph.User,
            graph.Membership,
            graph.Client,
            document);

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();

        LegalDocument persisted =
            await dbContext.LegalDocuments
                .AsNoTracking()
                .SingleAsync();

        Assert.Equal(graph.Client.Id, persisted.ClientId);
        Assert.Null(persisted.ProcessId);
    }

    [Fact]
    public async Task SaveChangesAsync_WithSameTenantProcess_PersistsProcessClassification()
    {
        SeedGraph graph = CreateGraph(
            includeClient: true,
            includeProcess: true);
        LegalDocument document = CreateDocument(
            graph.Organization.Id,
            graph.Membership.Id,
            processId: graph.Process!.Id);

        await SeedAsync(
            graph.Organization,
            graph.User,
            graph.Membership,
            graph.Client,
            graph.Process,
            document);

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();

        LegalDocument persisted =
            await dbContext.LegalDocuments
                .AsNoTracking()
                .SingleAsync();

        Assert.Null(persisted.ClientId);
        Assert.Equal(graph.Process.Id, persisted.ProcessId);
    }

    [Fact]
    public async Task SaveChangesAsync_WithCrossTenantClient_EnforcesCompositeForeignKey()
    {
        SeedGraph graphA = CreateGraph(
            "Alpha",
            "alpha");
        SeedGraph graphB = CreateGraph(
            "Beta",
            "beta",
            includeClient: true);

        await SeedAsync(
            graphA.Organization,
            graphA.User,
            graphA.Membership,
            graphB.Organization,
            graphB.User,
            graphB.Membership,
            graphB.Client);

        LegalDocument document = CreateDocument(
            graphA.Organization.Id,
            graphA.Membership.Id,
            clientId: graphB.Client!.Id);

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();
        dbContext.LegalDocuments.Add(document);

        DbUpdateException exception =
            await Assert.ThrowsAsync<DbUpdateException>(
                () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            "fk_legal_documents_clients_org_id_client_id");
    }

    [Fact]
    public async Task SaveChangesAsync_WithCrossTenantProcess_EnforcesCompositeForeignKey()
    {
        SeedGraph graphA = CreateGraph(
            "Alpha",
            "alpha");
        SeedGraph graphB = CreateGraph(
            "Beta",
            "beta",
            includeClient: true,
            includeProcess: true);

        await SeedAsync(
            graphA.Organization,
            graphA.User,
            graphA.Membership,
            graphB.Organization,
            graphB.User,
            graphB.Membership,
            graphB.Client,
            graphB.Process);

        LegalDocument document = CreateDocument(
            graphA.Organization.Id,
            graphA.Membership.Id,
            processId: graphB.Process!.Id);

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();
        dbContext.LegalDocuments.Add(document);

        DbUpdateException exception =
            await Assert.ThrowsAsync<DbUpdateException>(
                () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            "fk_legal_documents_processes_org_id_process_id");
    }

    [Fact]
    public async Task SaveChangesAsync_WithCrossTenantUploader_EnforcesCompositeForeignKey()
    {
        SeedGraph graphA = CreateGraph(
            "Alpha",
            "alpha");
        SeedGraph graphB = CreateGraph(
            "Beta",
            "beta");

        await SeedAsync(
            graphA.Organization,
            graphA.User,
            graphA.Membership,
            graphB.Organization,
            graphB.User,
            graphB.Membership);

        LegalDocument document = CreateDocument(
            graphA.Organization.Id,
            graphB.Membership.Id);

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();
        dbContext.LegalDocuments.Add(document);

        DbUpdateException exception =
            await Assert.ThrowsAsync<DbUpdateException>(
                () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            "fk_legal_documents_memberships_org_id_uploader_id");
    }

    [Fact]
    public async Task SaveChangesAsync_WithDuplicateOriginalFileNames_AllowsBothDocuments()
    {
        SeedGraph graph = CreateGraph();
        LegalDocument first = CreateDocument(
            graph.Organization.Id,
            graph.Membership.Id);
        LegalDocument second = CreateDocument(
            graph.Organization.Id,
            graph.Membership.Id,
            storedObjectKey:
                "fedcba9876543210fedcba9876543210");

        await SeedAsync(
            graph.Organization,
            graph.User,
            graph.Membership,
            first,
            second);

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();

        Assert.Equal(
            2,
            await dbContext.LegalDocuments.CountAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_WithDuplicateStoredObjectKey_EnforcesUniqueIndex()
    {
        SeedGraph graph = CreateGraph();
        await SeedAsync(
            graph.Organization,
            graph.User,
            graph.Membership);

        LegalDocument first = CreateDocument(
            graph.Organization.Id,
            graph.Membership.Id);
        LegalDocument second = CreateDocument(
            graph.Organization.Id,
            graph.Membership.Id);

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();
        dbContext.AddRange(first, second);

        DbUpdateException exception =
            await Assert.ThrowsAsync<DbUpdateException>(
                () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.UniqueViolation,
            "ux_legal_documents_stored_object_key");
    }

    [Fact]
    public async Task DatabaseInsert_WithClientAndProcess_EnforcesClassificationCheck()
    {
        SeedGraph graph = CreateGraph(
            includeClient: true,
            includeProcess: true);
        await SeedAsync(
            graph.Organization,
            graph.User,
            graph.Membership,
            graph.Client,
            graph.Process);

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();

        PostgresException exception =
            await Assert.ThrowsAsync<PostgresException>(
                () => dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO legal_documents (
                        id,
                        organization_id,
                        client_id,
                        process_id,
                        original_file_name,
                        stored_object_key,
                        content_type,
                        size_bytes,
                        content_hash_sha256,
                        uploaded_by_membership_id,
                        created_at)
                    VALUES (
                        {Guid.NewGuid()},
                        {graph.Organization.Id},
                        {graph.Client!.Id},
                        {graph.Process!.Id},
                        {"contract.pdf"},
                        {"11111111111111111111111111111111"},
                        {"application/pdf"},
                        {1234L},
                        {CreateHash().ToArray()},
                        {graph.Membership.Id},
                        {CreatedAt})
                    """));

        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            exception.SqlState);
        Assert.Equal(
            "ck_legal_documents_classification",
            exception.ConstraintName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(26214401)]
    public async Task DatabaseInsert_WithInvalidSize_EnforcesSizeCheck(
        long sizeBytes)
    {
        SeedGraph graph = CreateGraph();
        await SeedAsync(
            graph.Organization,
            graph.User,
            graph.Membership);

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();

        PostgresException exception =
            await Assert.ThrowsAsync<PostgresException>(
                () => dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO legal_documents (
                        id,
                        organization_id,
                        client_id,
                        process_id,
                        original_file_name,
                        stored_object_key,
                        content_type,
                        size_bytes,
                        content_hash_sha256,
                        uploaded_by_membership_id,
                        created_at)
                    VALUES (
                        {Guid.NewGuid()},
                        {graph.Organization.Id},
                        NULL,
                        NULL,
                        {"contract.pdf"},
                        {"22222222222222222222222222222222"},
                        {"application/pdf"},
                        {sizeBytes},
                        {CreateHash().ToArray()},
                        {graph.Membership.Id},
                        {CreatedAt})
                    """));

        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            exception.SqlState);
        Assert.Equal(
            "ck_legal_documents_size_bytes",
            exception.ConstraintName);
    }

    [Fact]
    public async Task DatabaseInsert_WithInvalidHashLength_EnforcesHashCheck()
    {
        SeedGraph graph = CreateGraph();
        await SeedAsync(
            graph.Organization,
            graph.User,
            graph.Membership);

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();

        PostgresException exception =
            await Assert.ThrowsAsync<PostgresException>(
                () => dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO legal_documents (
                        id,
                        organization_id,
                        client_id,
                        process_id,
                        original_file_name,
                        stored_object_key,
                        content_type,
                        size_bytes,
                        content_hash_sha256,
                        uploaded_by_membership_id,
                        created_at)
                    VALUES (
                        {Guid.NewGuid()},
                        {graph.Organization.Id},
                        NULL,
                        NULL,
                        {"contract.pdf"},
                        {"33333333333333333333333333333333"},
                        {"application/pdf"},
                        {1234L},
                        {new byte[31]},
                        {graph.Membership.Id},
                        {CreatedAt})
                    """));

        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            exception.SqlState);
        Assert.Equal(
            "ck_legal_documents_content_hash_sha256_length",
            exception.ConstraintName);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenDeletingReferencedClient_RestrictsDelete()
    {
        SeedGraph graph = CreateGraph(
            includeClient: true);
        LegalDocument document = CreateDocument(
            graph.Organization.Id,
            graph.Membership.Id,
            clientId: graph.Client!.Id);
        await SeedAsync(
            graph.Organization,
            graph.User,
            graph.Membership,
            graph.Client,
            document);

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();
        Client client =
            await dbContext.Clients.SingleAsync();
        dbContext.Clients.Remove(client);

        DbUpdateException exception =
            await Assert.ThrowsAsync<DbUpdateException>(
                () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.RestrictViolation,
            "fk_legal_documents_clients_org_id_client_id");
    }

    [Fact]
    public async Task SaveChangesAsync_WhenDeletingReferencedProcess_RestrictsDelete()
    {
        SeedGraph graph = CreateGraph(
            includeClient: true,
            includeProcess: true);
        LegalDocument document = CreateDocument(
            graph.Organization.Id,
            graph.Membership.Id,
            processId: graph.Process!.Id);
        await SeedAsync(
            graph.Organization,
            graph.User,
            graph.Membership,
            graph.Client,
            graph.Process,
            document);

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();
        LegalProcess legalProcess =
            await dbContext.LegalProcesses.SingleAsync();
        dbContext.LegalProcesses.Remove(legalProcess);

        DbUpdateException exception =
            await Assert.ThrowsAsync<DbUpdateException>(
                () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.RestrictViolation,
            "fk_legal_documents_processes_org_id_process_id");
    }

    [Fact]
    public async Task SaveChangesAsync_WhenDeletingUploaderMembership_RestrictsDelete()
    {
        SeedGraph graph = CreateGraph();
        LegalDocument document = CreateDocument(
            graph.Organization.Id,
            graph.Membership.Id);
        await SeedAsync(
            graph.Organization,
            graph.User,
            graph.Membership,
            document);

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();
        OrganizationMembership membership =
            await dbContext.OrganizationMemberships
                .SingleAsync();
        dbContext.OrganizationMemberships.Remove(
            membership);

        DbUpdateException exception =
            await Assert.ThrowsAsync<DbUpdateException>(
                () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.RestrictViolation,
            "fk_legal_documents_memberships_org_id_uploader_id");
    }

    [Fact]
    public async Task SaveChangesAsync_WhenDeactivatingRelatedRows_PreservesHistoricalDocument()
    {
        SeedGraph graph = CreateGraph(
            includeClient: true);
        LegalDocument document = CreateDocument(
            graph.Organization.Id,
            graph.Membership.Id,
            clientId: graph.Client!.Id);
        await SeedAsync(
            graph.Organization,
            graph.User,
            graph.Membership,
            graph.Client,
            document);

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();
        Client client = await dbContext.Clients.SingleAsync();
        OrganizationMembership membership =
            await dbContext.OrganizationMemberships.SingleAsync();

        client.Deactivate();
        membership.Deactivate();
        await dbContext.SaveChangesAsync();

        Assert.True(
            await dbContext.LegalDocuments.AnyAsync(
                candidate => candidate.Id == document.Id));
    }

    [Fact]
    public void LegalDocumentModel_HasTenantSafeRelationshipsConstraintsAndIndexes()
    {
        using EnmaDbContext dbContext =
            fixture.CreateDbContext();

        IEntityType? entityType =
            dbContext.Model.FindEntityType(
                typeof(LegalDocument));

        Assert.NotNull(entityType);
        Assert.Equal(
            "legal_documents",
            entityType.GetTableName());

        Assert.False(
            entityType.FindProperty(
                nameof(LegalDocument.OrganizationId))!
                .IsNullable);
        Assert.True(
            entityType.FindProperty(
                nameof(LegalDocument.ClientId))!
                .IsNullable);
        Assert.True(
            entityType.FindProperty(
                nameof(LegalDocument.ProcessId))!
                .IsNullable);
        Assert.False(
            entityType.FindProperty(
                nameof(LegalDocument.UploadedByMembershipId))!
                .IsNullable);
        Assert.Equal(
            255,
            entityType.FindProperty(
                nameof(LegalDocument.OriginalFileName))!
                .GetMaxLength());
        Assert.Equal(
            32,
            entityType.FindProperty(
                nameof(LegalDocument.StoredObjectKey))!
                .GetMaxLength());
        Assert.Equal(
            100,
            entityType.FindProperty(
                nameof(LegalDocument.ContentType))!
                .GetMaxLength());

        IKey alternateKey = Assert.Single(
            entityType.GetKeys(),
            key => !key.IsPrimaryKey());

        Assert.Equal(
            "ak_legal_documents_organization_id_id",
            alternateKey.GetName());
        Assert.Equal(
            [
                nameof(LegalDocument.OrganizationId),
                nameof(LegalDocument.Id)
            ],
            alternateKey.Properties
                .Select(property => property.Name)
                .ToArray());

        Assert.Equal(5, entityType.GetIndexes().Count());

        AssertIndex(
            entityType,
            "ux_legal_documents_stored_object_key",
            true,
            [nameof(LegalDocument.StoredObjectKey)]);

        AssertIndex(
            entityType,
            "ix_legal_documents_organization_id_created_at_id",
            false,
            [
                nameof(LegalDocument.OrganizationId),
                nameof(LegalDocument.CreatedAt),
                nameof(LegalDocument.Id)
            ]);

        AssertIndex(
            entityType,
            "ix_legal_documents_organization_id_client_id",
            false,
            [
                nameof(LegalDocument.OrganizationId),
                nameof(LegalDocument.ClientId)
            ]);

        AssertIndex(
            entityType,
            "ix_legal_documents_organization_id_process_id",
            false,
            [
                nameof(LegalDocument.OrganizationId),
                nameof(LegalDocument.ProcessId)
            ]);

        AssertIndex(
            entityType,
            "ix_legal_documents_org_id_uploaded_by_membership_id",
            false,
            [
                nameof(LegalDocument.OrganizationId),
                nameof(LegalDocument.UploadedByMembershipId)
            ]);

        AssertForeignKey(
            entityType,
            typeof(Organization),
            [nameof(LegalDocument.OrganizationId)]);

        AssertForeignKey(
            entityType,
            typeof(Client),
            [
                nameof(LegalDocument.OrganizationId),
                nameof(LegalDocument.ClientId)
            ]);

        AssertForeignKey(
            entityType,
            typeof(LegalProcess),
            [
                nameof(LegalDocument.OrganizationId),
                nameof(LegalDocument.ProcessId)
            ]);

        AssertForeignKey(
            entityType,
            typeof(OrganizationMembership),
            [
                nameof(LegalDocument.OrganizationId),
                nameof(LegalDocument.UploadedByMembershipId)
            ]);
    }

    [Fact]
    public async Task PostgreSqlSchema_WithLegalDocuments_HasExpectedTenantConstraints()
    {
        Assert.Equal(
            "RESTRICT",
            await GetDeleteRuleAsync(
                "fk_legal_documents_organizations_organization_id"));
        Assert.Equal(
            "RESTRICT",
            await GetDeleteRuleAsync(
                "fk_legal_documents_clients_org_id_client_id"));
        Assert.Equal(
            "RESTRICT",
            await GetDeleteRuleAsync(
                "fk_legal_documents_processes_org_id_process_id"));
        Assert.Equal(
            "RESTRICT",
            await GetDeleteRuleAsync(
                "fk_legal_documents_memberships_org_id_uploader_id"));

        Assert.Equal(
            "organization_id,id",
            await GetConstraintColumnsAsync(
                "legal_documents",
                "ak_legal_documents_organization_id_id",
                "UNIQUE"));

        Assert.NotNull(
            await GetIndexDefinitionAsync(
                "legal_documents",
                "ux_legal_documents_stored_object_key"));
        Assert.NotNull(
            await GetIndexDefinitionAsync(
                "legal_documents",
                "ix_legal_documents_organization_id_created_at_id"));
        Assert.NotNull(
            await GetIndexDefinitionAsync(
                "legal_documents",
                "ix_legal_documents_organization_id_client_id"));
        Assert.NotNull(
            await GetIndexDefinitionAsync(
                "legal_documents",
                "ix_legal_documents_organization_id_process_id"));
        Assert.NotNull(
            await GetIndexDefinitionAsync(
                "legal_documents",
                "ix_legal_documents_org_id_uploaded_by_membership_id"));
    }

    private async Task SeedAsync(params object?[] entities)
    {
        object[] nonNullEntities = entities
            .Where(entity => entity is not null)
            .Cast<object>()
            .ToArray();

        await using EnmaDbContext dbContext =
            fixture.CreateDbContext();
        dbContext.AddRange(nonNullEntities);
        await dbContext.SaveChangesAsync();
    }

    private static SeedGraph CreateGraph(
        string organizationName = "Enma Legal",
        string organizationSlug = "enma-legal",
        bool includeClient = false,
        bool includeProcess = false)
    {
        var organization = new Organization(
            organizationName,
            organizationSlug,
            CreatedAt);
        var user = new User(
            $"{organizationName} User",
            $"{organizationSlug}@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);

        Client? client = includeClient || includeProcess
            ? new Client(
                organization.Id,
                $"{organizationName} Client",
                CreatedAt)
            : null;

        LegalProcess? legalProcess = includeProcess
            ? new LegalProcess(
                organization.Id,
                client!.Id,
                $"{organizationName} Process",
                CreatedAt)
            : null;

        return new SeedGraph(
            organization,
            user,
            membership,
            client,
            legalProcess);
    }

    private static LegalDocument CreateDocument(
        Guid organizationId,
        Guid membershipId,
        Guid? clientId = null,
        Guid? processId = null,
        string storedObjectKey =
            "0123456789abcdef0123456789abcdef")
    {
        return new LegalDocument(
            organizationId,
            clientId,
            processId,
            "contract.pdf",
            storedObjectKey,
            "application/pdf",
            1234,
            CreateHash(),
            membershipId,
            CreatedAt);
    }

    private static LegalDocumentContentHash CreateHash()
    {
        return new LegalDocumentContentHash(
            Enumerable.Range(0, 32)
                .Select(value => (byte)value)
                .ToArray());
    }

    private async Task<string?> GetDeleteRuleAsync(
        string constraintName)
    {
        const string Query =
            """
            SELECT delete_rule
            FROM information_schema.referential_constraints
            WHERE constraint_schema = 'public'
              AND constraint_name = @constraintName
            """;

        return await ExecuteScalarStringAsync(
            Query,
            constraintName);
    }

    private async Task<string?> GetConstraintColumnsAsync(
        string tableName,
        string constraintName,
        string constraintType)
    {
        const string Query =
            """
            SELECT string_agg(
                kcu.column_name,
                ','
                ORDER BY kcu.ordinal_position)
            FROM information_schema.table_constraints AS tc
            INNER JOIN information_schema.key_column_usage AS kcu
                ON kcu.constraint_schema = tc.constraint_schema
                AND kcu.constraint_name = tc.constraint_name
            WHERE tc.constraint_schema = 'public'
              AND tc.table_name = @tableName
              AND tc.constraint_name = @constraintName
              AND tc.constraint_type = @constraintType
            """;

        await using var connection =
            new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command =
            new NpgsqlCommand(Query, connection);
        command.Parameters.AddWithValue(
            "tableName",
            tableName);
        command.Parameters.AddWithValue(
            "constraintName",
            constraintName);
        command.Parameters.AddWithValue(
            "constraintType",
            constraintType);

        object? result = await command.ExecuteScalarAsync();

        return result is DBNull or null
            ? null
            : (string)result;
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

        await using var connection =
            new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command =
            new NpgsqlCommand(Query, connection);
        command.Parameters.AddWithValue(
            "tableName",
            tableName);
        command.Parameters.AddWithValue(
            "indexName",
            indexName);

        object? result = await command.ExecuteScalarAsync();

        return result is null
            ? null
            : (string)result;
    }

    private async Task<string?> ExecuteScalarStringAsync(
        string query,
        string constraintName)
    {
        await using var connection =
            new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command =
            new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue(
            "constraintName",
            constraintName);

        object? result = await command.ExecuteScalarAsync();

        return result is null
            ? null
            : (string)result;
    }

    private static void AssertPostgresException(
        DbUpdateException exception,
        string expectedSqlState,
        string expectedConstraintName)
    {
        PostgresException postgresException =
            Assert.IsType<PostgresException>(
                exception.InnerException);

        Assert.Equal(
            expectedSqlState,
            postgresException.SqlState);
        Assert.Equal(
            expectedConstraintName,
            postgresException.ConstraintName);
    }

    private static void AssertIndex(
        IEntityType entityType,
        string databaseName,
        bool isUnique,
        string[] propertyNames)
    {
        IIndex index = Assert.Single(
            entityType.GetIndexes(),
            candidate =>
                candidate.GetDatabaseName()
                    == databaseName);

        Assert.Equal(isUnique, index.IsUnique);
        Assert.Equal(
            propertyNames,
            index.Properties
                .Select(property => property.Name)
                .ToArray());
    }

    private static void AssertForeignKey(
        IEntityType entityType,
        Type principalType,
        string[] propertyNames)
    {
        IForeignKey foreignKey = Assert.Single(
            entityType.GetForeignKeys(),
            candidate =>
                candidate.PrincipalEntityType.ClrType
                    == principalType);

        Assert.Equal(
            DeleteBehavior.Restrict,
            foreignKey.DeleteBehavior);
        Assert.Equal(
            propertyNames,
            foreignKey.Properties
                .Select(property => property.Name)
                .ToArray());
    }

    private sealed record SeedGraph(
        Organization Organization,
        User User,
        OrganizationMembership Membership,
        Client? Client,
        LegalProcess? Process);
}
