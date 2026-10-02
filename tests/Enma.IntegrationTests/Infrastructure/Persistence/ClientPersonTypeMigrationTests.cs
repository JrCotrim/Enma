using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class ClientPersonTypeMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration =
        "20261001141525_ExtendAuditTaxonomyForLegalDeadlineResponsible";
    private const string CurrentMigration =
        "20261002191138_AddClientPersonTypeAndDocuments";
    private const string FirstCpf = "52998224725";
    private const string FormattedFirstCpf = "529.982.247-25";
    private const string SecondCpf = "12345678909";
    private const string FormattedSecondCpf = "123.456.789-09";
    private const string ValidCnpj = "11222333000181";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        10,
        2,
        12,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    // Clients are removed first so a failed duplicate scenario cannot keep the
    // shared database below the latest migration.
    public async Task DisposeAsync()
    {
        await DeleteAllClientsAsync();
        await MigrateAsync();
    }

    [Fact]
    public async Task MigrateAsync_FromPreviousSchema_BackfillsLegacyClientsAsIndividuals()
    {
        await MigrateAsync(PreviousMigration);
        Organization organization = CreateOrganization("legacy");
        var clientWithCpf = new Client(
            organization.Id,
            "Legacy With Cpf",
            CreatedAt,
            "legacy@example.test",
            "22988887777",
            FirstCpf);
        var clientWithoutCpf = new Client(
            organization.Id,
            "Legacy Without Cpf",
            CreatedAt.AddMinutes(1));
        var inactiveClientWithCpf = new Client(
            organization.Id,
            "Legacy Inactive With Cpf",
            CreatedAt.AddMinutes(2),
            cpf: SecondCpf);
        inactiveClientWithCpf.Deactivate();
        await SeedLegacyAsync(
            organization,
            clientWithCpf,
            clientWithoutCpf,
            inactiveClientWithCpf);

        Assert.False(await ColumnExistsAsync("person_type"));

        await MigrateAsync();

        Assert.Equal(
            [
                $"{clientWithCpf.Id}|1|{FirstCpf}|||",
                $"{clientWithoutCpf.Id}|1||||",
                $"{inactiveClientWithCpf.Id}|1|{SecondCpf}|||"
            ],
            await QueryClientRowsAsync());

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Client[] persisted = await dbContext.Clients
            .AsNoTracking()
            .OrderBy(client => client.CreatedAt)
            .ToArrayAsync();
        Assert.Equal(3, persisted.Length);
        Assert.All(persisted, client =>
        {
            Assert.Equal(PersonType.Individual, client.PersonType);
            Assert.Null(client.Cnpj);
            Assert.Null(client.Address);
            Assert.Null(client.Notes);
        });
        Assert.Equal(FirstCpf, persisted[0].Cpf);
        Assert.Equal("legacy@example.test", persisted[0].Email);
        Assert.Equal("22988887777", persisted[0].Phone);
        Assert.Null(persisted[1].Cpf);
        Assert.Equal(SecondCpf, persisted[2].Cpf);
        Assert.False(persisted[2].IsActive);
        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task MigrateAsync_WithDuplicateCpfInSameOrganization_FailsReportingOnlyCounts()
    {
        await MigrateAsync(PreviousMigration);
        Organization firstOrganization = CreateOrganization("duplicate-a");
        Organization secondOrganization = CreateOrganization("duplicate-b");
        var activeDuplicate = new Client(
            firstOrganization.Id,
            "Active Duplicate",
            CreatedAt,
            cpf: FirstCpf);
        var inactiveDuplicate = new Client(
            firstOrganization.Id,
            "Inactive Duplicate",
            CreatedAt.AddMinutes(1),
            cpf: FirstCpf);
        inactiveDuplicate.Deactivate();
        var firstOrganizationUnique = new Client(
            firstOrganization.Id,
            "Unique In First Organization",
            CreatedAt,
            cpf: SecondCpf);
        Client[] secondOrganizationDuplicates =
        [
            new Client(secondOrganization.Id, "Second A", CreatedAt, cpf: SecondCpf),
            new Client(secondOrganization.Id, "Second B", CreatedAt, cpf: SecondCpf),
            new Client(secondOrganization.Id, "Second C", CreatedAt, cpf: SecondCpf)
        ];
        var secondOrganizationUnique = new Client(
            secondOrganization.Id,
            "Unique In Second Organization",
            CreatedAt,
            cpf: FirstCpf);
        var secondOrganizationWithoutCpf = new Client(
            secondOrganization.Id,
            "Without Cpf",
            CreatedAt);
        await SeedLegacyAsync(
            firstOrganization,
            activeDuplicate,
            inactiveDuplicate,
            firstOrganizationUnique);
        await SeedLegacyAsync(
            secondOrganization,
            [
                .. secondOrganizationDuplicates,
                secondOrganizationUnique,
                secondOrganizationWithoutCpf
            ]);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => MigrateAsync());

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal(
            "clients has duplicate documents within an organization: "
                + "2 CPF group(s) covering 5 client(s); "
                + "0 CNPJ group(s) covering 0 client(s)",
            exception.MessageText);
        string diagnostics = string.Join(
            "\n",
            exception.ToString(),
            exception.MessageText,
            exception.Detail,
            exception.Hint,
            exception.Where,
            exception.InternalQuery);
        Assert.DoesNotContain(FirstCpf, diagnostics);
        Assert.DoesNotContain(FormattedFirstCpf, diagnostics);
        Assert.DoesNotContain(SecondCpf, diagnostics);
        Assert.DoesNotContain(FormattedSecondCpf, diagnostics);

        Assert.False(await ColumnExistsAsync("person_type"));
        Assert.False(await MigrationAppliedAsync(CurrentMigration));
        Assert.Equal(8, await CountClientsAsync());

        await DeleteClientsAsync(
            inactiveDuplicate.Id,
            secondOrganizationDuplicates[1].Id,
            secondOrganizationDuplicates[2].Id);

        await MigrateAsync();

        Assert.True(await MigrationAppliedAsync(CurrentMigration));
        Assert.Equal(5, await CountClientsAsync());
    }

    [Fact]
    public async Task MigrateAsync_WithSameCpfInDifferentOrganizations_Succeeds()
    {
        await MigrateAsync(PreviousMigration);
        Organization firstOrganization = CreateOrganization("shared-a");
        Organization secondOrganization = CreateOrganization("shared-b");
        var firstClient = new Client(
            firstOrganization.Id,
            "Shared Cpf A",
            CreatedAt,
            cpf: FirstCpf);
        var secondClient = new Client(
            secondOrganization.Id,
            "Shared Cpf B",
            CreatedAt.AddMinutes(1),
            cpf: FirstCpf);
        await SeedLegacyAsync(firstOrganization, firstClient);
        await SeedLegacyAsync(secondOrganization, secondClient);

        await MigrateAsync();

        Assert.Equal(
            [
                $"{firstClient.Id}|1|{FirstCpf}|||",
                $"{secondClient.Id}|1|{FirstCpf}|||"
            ],
            await QueryClientRowsAsync());
    }

    [Fact]
    public async Task CurrentSchema_HasExpectedColumnsChecksAndIndexes()
    {
        Assert.Equal(
            [
                "address|character varying|300|YES|",
                "cnpj|character varying|14|YES|",
                "notes|character varying|2000|YES|",
                "person_type|smallint||NO|"
            ],
            await QueryNewColumnsAsync());

        Assert.Equal(
            [
                "ck_clients_address_normalized|CHECK (((address IS NULL) OR (((address)::text = btrim((address)::text)) AND (length((address)::text) > 0))))",
                "ck_clients_cnpj_normalized|CHECK (((cnpj IS NULL) OR (((cnpj)::text COLLATE \"C\") ~ '^[0-9A-Z]{12}[0-9]{2}$'::text)))",
                "ck_clients_cpf_normalized|CHECK (((cpf IS NULL) OR ((cpf)::text ~ '^[0-9]{11}$'::text)))",
                "ck_clients_document_matches_person_type|CHECK ((((person_type = 1) AND (cnpj IS NULL)) OR ((person_type = 2) AND (cpf IS NULL))))",
                "ck_clients_email_normalized|CHECK (((email IS NULL) OR (((email)::text = lower(btrim((email)::text))) AND ((length((email)::text) >= 3) AND (length((email)::text) <= 254)))))",
                "ck_clients_notes_normalized|CHECK (((notes IS NULL) OR (((notes)::text = btrim((notes)::text)) AND (length((notes)::text) > 0))))",
                "ck_clients_person_type|CHECK ((person_type = ANY (ARRAY[1, 2])))",
                "ck_clients_phone_normalized|CHECK (((phone IS NULL) OR ((phone)::text ~ '^[0-9]{8,15}$'::text)))"
            ],
            await QueryCheckConstraintsAsync());

        Assert.Equal(
            [
                "ux_clients_organization_id_cnpj|CREATE UNIQUE INDEX ux_clients_organization_id_cnpj ON public.clients USING btree (organization_id, cnpj) WHERE (cnpj IS NOT NULL)",
                "ux_clients_organization_id_cpf|CREATE UNIQUE INDEX ux_clients_organization_id_cpf ON public.clients USING btree (organization_id, cpf) WHERE (cpf IS NOT NULL)"
            ],
            await QueryDocumentIndexesAsync());
    }

    [Fact]
    public async Task MigrateAsync_DownAndUp_DropsNewDataPreservesCpfAndRestoresSchema()
    {
        Organization organization = CreateOrganization("round-trip");
        var individual = new Client(
            organization.Id,
            "Round Trip Individual",
            CreatedAt,
            cpf: FirstCpf,
            address: "Rua das Flores, 100",
            notes: "Individual notes");
        var company = new Client(
            organization.Id,
            "Round Trip Company",
            CreatedAt.AddMinutes(1),
            personType: PersonType.Company,
            cnpj: ValidCnpj,
            address: "Av. Central, 1",
            notes: "Company notes");
        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.AddRange(organization, individual, company);
            await seedContext.SaveChangesAsync();
        }

        await MigrateAsync(PreviousMigration);

        Assert.Empty(await QueryNewColumnsAsync());
        Assert.Empty(await QueryDocumentIndexesAsync());
        Assert.DoesNotContain(
            await QueryCheckConstraintsAsync(),
            constraint => constraint.StartsWith("ck_clients_person_type|", StringComparison.Ordinal) ||
                constraint.StartsWith("ck_clients_cnpj_normalized|", StringComparison.Ordinal) ||
                constraint.StartsWith("ck_clients_document_matches_person_type|", StringComparison.Ordinal) ||
                constraint.StartsWith("ck_clients_address_normalized|", StringComparison.Ordinal) ||
                constraint.StartsWith("ck_clients_notes_normalized|", StringComparison.Ordinal));
        Assert.Equal(2, await CountClientsAsync());

        await MigrateAsync();

        Assert.Equal(
            [
                $"{individual.Id}|1|{FirstCpf}|||",
                $"{company.Id}|1||||"
            ],
            await QueryClientRowsAsync());
        Assert.Equal(2, (await QueryDocumentIndexesAsync()).Length);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    private static Organization CreateOrganization(string slug)
    {
        return new Organization(
            $"Client Person Type {slug}",
            $"client-person-type-{slug}",
            CreatedAt);
    }

    private async Task SeedLegacyAsync(
        Organization organization,
        params Client[] clients)
    {
        await using EnmaDbContext seedContext = fixture.CreateDbContext();
        seedContext.Add(organization);
        await seedContext.SaveChangesAsync();

        foreach (Client client in clients)
        {
            await PostgreSqlFixture.InsertClientWithoutPersonTypeColumnsAsync(
                seedContext,
                client);
        }
    }

    private async Task<string[]> QueryClientRowsAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT concat_ws(
                '|',
                id::text,
                person_type::text,
                coalesce(cpf, ''),
                coalesce(cnpj, ''),
                coalesce(address, ''),
                coalesce(notes, ''))
            FROM clients
            ORDER BY created_at, id
            """,
            connection);

        return await ReadStringsAsync(command);
    }

    private async Task<string[]> QueryNewColumnsAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT concat_ws(
                '|',
                column_name,
                data_type,
                coalesce(character_maximum_length::text, ''),
                is_nullable,
                coalesce(column_default, ''))
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'clients'
              AND column_name IN ('person_type', 'cnpj', 'address', 'notes')
            ORDER BY column_name
            """,
            connection);

        return await ReadStringsAsync(command);
    }

    private async Task<string[]> QueryCheckConstraintsAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT conname || '|' || pg_get_constraintdef(oid)
            FROM pg_constraint
            WHERE conrelid = 'public.clients'::regclass
              AND contype = 'c'
            ORDER BY conname
            """,
            connection);

        return await ReadStringsAsync(command);
    }

    private async Task<string[]> QueryDocumentIndexesAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT indexname || '|' || indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'clients'
              AND indexname IN (
                  'ux_clients_organization_id_cpf',
                  'ux_clients_organization_id_cnpj')
            ORDER BY indexname
            """,
            connection);

        return await ReadStringsAsync(command);
    }

    private async Task<bool> ColumnExistsAsync(string columnName)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'clients'
                  AND column_name = @columnName
            )
            """,
            connection);
        command.Parameters.AddWithValue("columnName", columnName);

        return Assert.IsType<bool>(await command.ExecuteScalarAsync());
    }

    private async Task<bool> MigrationAppliedAsync(string migrationId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = @migrationId
            )
            """,
            connection);
        command.Parameters.AddWithValue("migrationId", migrationId);

        return Assert.IsType<bool>(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountClientsAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::integer FROM clients",
            connection);

        return Assert.IsType<int>(await command.ExecuteScalarAsync());
    }

    private async Task DeleteClientsAsync(params Guid[] clientIds)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM clients WHERE id = ANY(@clientIds)",
            connection);
        command.Parameters.AddWithValue("clientIds", clientIds);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteAllClientsAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM clients",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> ReadStringsAsync(NpgsqlCommand command)
    {
        var values = new List<string>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private async Task MigrateAsync(string? targetMigration = null)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        IMigrator migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }
}
