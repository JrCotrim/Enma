using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationMembershipMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration =
        "20260813195331_AddLegalDeadlines";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        14,
        12,
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
    public async Task MigrateAsync_FromEmptyDatabase_CreatesUsableMembershipRelationalKey()
    {
        await MigrateAsync(Migration.InitialDatabase);
        await MigrateAsync();
        var graph = CreateRepresentativeGraph();

        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            dbContext.AddRange(
                graph.Organization,
                graph.User,
                graph.Membership,
                graph.Client,
                graph.LegalProcess,
                graph.LegalDeadline);
            await dbContext.SaveChangesAsync();
        }

        await AssertMembershipRelationalKeyAsync(
            graph.Organization.Id,
            graph.Membership.Id);
        string[] tables = await GetPublicTablesAsync();
        Assert.Contains("organizations", tables);
        Assert.Contains("users", tables);
        Assert.Contains("organization_memberships", tables);
        Assert.Contains("clients", tables);
        Assert.Contains("legal_processes", tables);
        Assert.Contains("legal_deadlines", tables);
    }

    [Fact]
    public async Task MigrateAsync_FromPreP0ASchema_PreservesRepresentativeDataAndSchema()
    {
        await MigrateAsync(PreviousMigration);
        var graph = CreateRepresentativeGraph();

        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.AddRange(
                graph.Organization,
                graph.User,
                graph.Membership,
                graph.Client,
                graph.LegalProcess,
                graph.LegalDeadline);
            await seedContext.SaveChangesAsync();
        }

        string[] tablesBefore = await GetPublicTablesAsync();

        await MigrateAsync();

        Assert.Equal(
            tablesBefore.Append("legal_tasks").OrderBy(table => table),
            await GetPublicTablesAsync());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = await dbContext.Organizations.SingleAsync();
        User user = await dbContext.Users.SingleAsync();
        OrganizationMembership membership =
            await dbContext.OrganizationMemberships.SingleAsync();
        Client client = await dbContext.Clients.SingleAsync();
        LegalProcess legalProcess = await dbContext.LegalProcesses.SingleAsync();
        LegalDeadline legalDeadline = await dbContext.LegalDeadlines.SingleAsync();

        Assert.Equal(graph.Organization.Id, organization.Id);
        Assert.Equal(graph.User.Id, user.Id);
        Assert.Equal(graph.Membership.Id, membership.Id);
        Assert.Equal(graph.Organization.Id, membership.OrganizationId);
        Assert.Equal(graph.Client.Id, client.Id);
        Assert.Equal(graph.LegalProcess.Id, legalProcess.Id);
        Assert.Equal(graph.LegalDeadline.Id, legalDeadline.Id);
        await AssertMembershipRelationalKeyAsync(
            organization.Id,
            membership.Id);
    }

    private async Task MigrateAsync(string? targetMigration = null)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        IMigrator migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private async Task AssertMembershipRelationalKeyAsync(
        Guid organizationId,
        Guid membershipId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        Assert.Equal(
            "organization_id,id",
            await GetConstraintColumnsAsync(
                connection,
                "ak_organization_memberships_organization_id_id",
                "UNIQUE"));
        string? organizationUserIndex = await GetIndexDefinitionAsync(
            connection,
            "ux_organization_memberships_organization_id_user_id");
        Assert.NotNull(organizationUserIndex);
        Assert.Contains("UNIQUE INDEX", organizationUserIndex);
        Assert.Contains("(organization_id, user_id)", organizationUserIndex);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync();

        await using (var createCommand = new NpgsqlCommand(
            """
            CREATE TABLE membership_reference_probe
            (
                organization_id uuid NOT NULL,
                membership_id uuid NOT NULL,
                CONSTRAINT fk_membership_reference_probe
                    FOREIGN KEY (organization_id, membership_id)
                    REFERENCES organization_memberships (organization_id, id)
            )
            """,
            connection,
            transaction))
        {
            await createCommand.ExecuteNonQueryAsync();
        }

        await using (var validCommand = new NpgsqlCommand(
            """
            INSERT INTO membership_reference_probe
                (organization_id, membership_id)
            VALUES (@organizationId, @membershipId)
            """,
            connection,
            transaction))
        {
            validCommand.Parameters.AddWithValue(
                "organizationId",
                organizationId);
            validCommand.Parameters.AddWithValue("membershipId", membershipId);
            await validCommand.ExecuteNonQueryAsync();
        }

        await using var invalidCommand = new NpgsqlCommand(
            """
            INSERT INTO membership_reference_probe
                (organization_id, membership_id)
            VALUES (@organizationId, @membershipId)
            """,
            connection,
            transaction);
        invalidCommand.Parameters.AddWithValue("organizationId", Guid.NewGuid());
        invalidCommand.Parameters.AddWithValue("membershipId", membershipId);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => invalidCommand.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal("fk_membership_reference_probe", exception.ConstraintName);
        await transaction.RollbackAsync();
    }

    private static async Task<string?> GetConstraintColumnsAsync(
        NpgsqlConnection connection,
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
              AND tc.table_name = 'organization_memberships'
              AND tc.constraint_name = @constraintName
              AND tc.constraint_type = @constraintType
            """;

        await using var command = new NpgsqlCommand(Query, connection);
        command.Parameters.AddWithValue("constraintName", constraintName);
        command.Parameters.AddWithValue("constraintType", constraintType);
        object? result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    private static async Task<string?> GetIndexDefinitionAsync(
        NpgsqlConnection connection,
        string indexName)
    {
        const string Query =
            """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'organization_memberships'
              AND indexname = @indexName
            """;

        await using var command = new NpgsqlCommand(Query, connection);
        command.Parameters.AddWithValue("indexName", indexName);
        object? result = await command.ExecuteScalarAsync();
        return result is null ? null : (string)result;
    }

    private async Task<string[]> GetPublicTablesAsync()
    {
        const string Query =
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_type = 'BASE TABLE'
            ORDER BY table_name
            """;

        var tables = new List<string>();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables.ToArray();
    }

    private static (
        Organization Organization,
        User User,
        OrganizationMembership Membership,
        Client Client,
        LegalProcess LegalProcess,
        LegalDeadline LegalDeadline) CreateRepresentativeGraph()
    {
        var organization = new Organization(
            "Enma Legal",
            "enma-legal",
            CreatedAt);
        var user = new User(
            "Current User",
            "current@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var client = new Client(
            organization.Id,
            "Existing Client",
            CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            "Existing Process",
            CreatedAt);
        var legalDeadline = new LegalDeadline(
            organization.Id,
            legalProcess.Id,
            "Existing Deadline",
            new DateOnly(2026, 11, 1),
            CreatedAt);

        return (
            organization,
            user,
            membership,
            client,
            legalProcess,
            legalDeadline);
    }
}
