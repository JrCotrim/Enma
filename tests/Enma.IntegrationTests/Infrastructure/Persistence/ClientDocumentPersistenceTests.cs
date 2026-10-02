using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class ClientDocumentPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string ValidCpf = "52998224725";
    private const string ValidCnpj = "11222333000181";
    private const string ValidAlphanumericCnpj = "12ABC34501DE35";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        10,
        2,
        15,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SaveChangesAsync_WithCompanyClient_RoundTripsNewFields()
    {
        Organization organization = CreateOrganization("round-trip");
        var company = new Client(
            organization.Id,
            "Acme Comercio Ltda",
            CreatedAt,
            personType: PersonType.Company,
            cnpj: "12.abc.345/01de-35",
            address: "Av. Central, 1",
            notes: "Key account");
        var individual = new Client(
            organization.Id,
            "Maria Silva",
            CreatedAt.AddMinutes(1),
            cpf: ValidCpf);
        await SeedAsync(organization, company, individual);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Client persistedCompany = await dbContext.Clients
            .AsNoTracking()
            .SingleAsync(client => client.Id == company.Id);
        Client persistedIndividual = await dbContext.Clients
            .AsNoTracking()
            .SingleAsync(client => client.Id == individual.Id);

        Assert.Equal(PersonType.Company, persistedCompany.PersonType);
        Assert.Equal(ValidAlphanumericCnpj, persistedCompany.Cnpj);
        Assert.Null(persistedCompany.Cpf);
        Assert.Equal("Av. Central, 1", persistedCompany.Address);
        Assert.Equal("Key account", persistedCompany.Notes);
        Assert.Equal(PersonType.Individual, persistedIndividual.PersonType);
        Assert.Equal(ValidCpf, persistedIndividual.Cpf);
        Assert.Null(persistedIndividual.Cnpj);
        Assert.Null(persistedIndividual.Address);
        Assert.Null(persistedIndividual.Notes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveChangesAsync_WithDuplicateCpfInSameOrganization_ViolatesCpfIndex(
        bool existingClientIsActive)
    {
        Organization organization = CreateOrganization("duplicate-cpf");
        var existing = new Client(
            organization.Id,
            "Existing Individual",
            CreatedAt,
            cpf: ValidCpf);

        if (!existingClientIsActive)
        {
            existing.Deactivate();
        }

        await SeedAsync(organization, existing);
        var duplicate = new Client(
            organization.Id,
            "Duplicate Individual",
            CreatedAt.AddMinutes(1),
            cpf: "529.982.247-25");

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => SeedAsync(duplicate));

        AssertPostgresException(
            exception,
            PostgresErrorCodes.UniqueViolation,
            "ux_clients_organization_id_cpf");
        Assert.Equal(1, await CountClientsAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveChangesAsync_WithDuplicateCnpjInSameOrganization_ViolatesCnpjIndex(
        bool existingClientIsActive)
    {
        Organization organization = CreateOrganization("duplicate-cnpj");
        var existing = new Client(
            organization.Id,
            "Existing Company",
            CreatedAt,
            personType: PersonType.Company,
            cnpj: ValidAlphanumericCnpj);

        if (!existingClientIsActive)
        {
            existing.Deactivate();
        }

        await SeedAsync(organization, existing);
        var duplicate = new Client(
            organization.Id,
            "Duplicate Company",
            CreatedAt.AddMinutes(1),
            personType: PersonType.Company,
            cnpj: "12.abc.345/01de-35");

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => SeedAsync(duplicate));

        AssertPostgresException(
            exception,
            PostgresErrorCodes.UniqueViolation,
            "ux_clients_organization_id_cnpj");
        Assert.Equal(1, await CountClientsAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_WithSameDocumentsInAnotherOrganization_AllowsClients()
    {
        Organization firstOrganization = CreateOrganization("first");
        Organization secondOrganization = CreateOrganization("second");
        await SeedAsync(
            firstOrganization,
            secondOrganization,
            new Client(firstOrganization.Id, "First Individual", CreatedAt, cpf: ValidCpf),
            new Client(
                firstOrganization.Id,
                "First Company",
                CreatedAt,
                personType: PersonType.Company,
                cnpj: ValidCnpj));

        await SeedAsync(
            new Client(secondOrganization.Id, "Second Individual", CreatedAt, cpf: ValidCpf),
            new Client(
                secondOrganization.Id,
                "Second Company",
                CreatedAt,
                personType: PersonType.Company,
                cnpj: ValidCnpj));

        Assert.Equal(4, await CountClientsAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_WithManyClientsWithoutDocuments_AllowsClients()
    {
        Organization organization = CreateOrganization("without-documents");
        await SeedAsync(
            organization,
            new Client(organization.Id, "Individual A", CreatedAt),
            new Client(organization.Id, "Individual B", CreatedAt),
            new Client(
                organization.Id,
                "Company A",
                CreatedAt,
                personType: PersonType.Company),
            new Client(
                organization.Id,
                "Company B",
                CreatedAt,
                personType: PersonType.Company));

        Assert.Equal(4, await CountClientsAsync());
    }

    [Theory]
    [InlineData("12abc34501de35")]
    [InlineData("12ABC34501DE3A")]
    [InlineData("1122233300018")]
    [InlineData("11.222.333/000")]
    [InlineData("11222333 00018")]
    [InlineData("\u00C92ABC34501DE35")]
    [InlineData("\uFF111222333000181")]
    public async Task DatabaseInsert_WithCnpjOutsideNormalizedFormat_ViolatesCnpjCheck(
        string cnpj)
    {
        Organization organization = await SeedOrganizationAsync();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => InsertClientAsync(organization.Id, personType: 2, cnpj: cnpj));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_clients_cnpj_normalized", exception.ConstraintName);
    }

    [Theory]
    [InlineData((short)0)]
    [InlineData((short)3)]
    [InlineData((short)-1)]
    public async Task DatabaseInsert_WithInvalidPersonType_ViolatesCheck(short personType)
    {
        Organization organization = await SeedOrganizationAsync();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => InsertClientAsync(organization.Id, personType));

        // An undefined person type matches neither branch of the document rule
        // either, so PostgreSQL may report whichever check it evaluates first.
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Contains(
            exception.ConstraintName,
            new[]
            {
                "ck_clients_person_type",
                "ck_clients_document_matches_person_type"
            });
    }

    [Fact]
    public async Task DatabaseInsert_WithNullPersonType_EnforcesNotNull()
    {
        Organization organization = await SeedOrganizationAsync();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => InsertClientAsync(organization.Id, personType: null));

        Assert.Equal(PostgresErrorCodes.NotNullViolation, exception.SqlState);
        Assert.Equal("person_type", exception.ColumnName);
    }

    [Theory]
    [InlineData((short)1, null, ValidCnpj)]
    [InlineData((short)1, ValidCpf, ValidCnpj)]
    [InlineData((short)2, ValidCpf, null)]
    [InlineData((short)2, ValidCpf, ValidCnpj)]
    public async Task DatabaseInsert_WithDocumentIncompatibleWithPersonType_ViolatesCheck(
        short personType,
        string? cpf,
        string? cnpj)
    {
        Organization organization = await SeedOrganizationAsync();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => InsertClientAsync(organization.Id, personType, cpf, cnpj));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(
            "ck_clients_document_matches_person_type",
            exception.ConstraintName);
    }

    [Theory]
    [InlineData((short)1, ValidCpf, null)]
    [InlineData((short)1, null, null)]
    [InlineData((short)2, null, ValidCnpj)]
    [InlineData((short)2, null, ValidAlphanumericCnpj)]
    [InlineData((short)2, null, null)]
    public async Task DatabaseInsert_WithDocumentCompatibleWithPersonType_Succeeds(
        short personType,
        string? cpf,
        string? cnpj)
    {
        Organization organization = await SeedOrganizationAsync();

        await InsertClientAsync(organization.Id, personType, cpf, cnpj);

        Assert.Equal(1, await CountClientsAsync());
    }

    [Theory]
    [InlineData("address", "")]
    [InlineData("address", " Rua das Flores")]
    [InlineData("address", "Rua das Flores ")]
    [InlineData("notes", "")]
    [InlineData("notes", " Key account")]
    [InlineData("notes", "Key account ")]
    public async Task DatabaseInsert_WithUntrimmedOrEmptyText_ViolatesNormalizedCheck(
        string column,
        string value)
    {
        Organization organization = await SeedOrganizationAsync();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => column == "address"
                ? InsertClientAsync(organization.Id, 1, address: value)
                : InsertClientAsync(organization.Id, 1, notes: value));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal($"ck_clients_{column}_normalized", exception.ConstraintName);
    }

    [Fact]
    public async Task DatabaseInsert_WithTextBeyondMaximumLength_EnforcesVarcharLimits()
    {
        Organization organization = await SeedOrganizationAsync();

        PostgresException address = await Assert.ThrowsAsync<PostgresException>(
            () => InsertClientAsync(
                organization.Id,
                1,
                address: new string('a', 301)));
        PostgresException notes = await Assert.ThrowsAsync<PostgresException>(
            () => InsertClientAsync(
                organization.Id,
                1,
                notes: new string('n', 2_001)));

        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, address.SqlState);
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, notes.SqlState);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private async Task<Organization> SeedOrganizationAsync()
    {
        Organization organization = CreateOrganization("raw-insert");
        await SeedAsync(organization);

        return organization;
    }

    private async Task InsertClientAsync(
        Guid organizationId,
        short? personType,
        string? cpf = null,
        string? cnpj = null,
        string? address = null,
        string? notes = null)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO clients
                (id, organization_id, name, cpf, person_type, cnpj, address,
                 notes, is_active, created_at)
            VALUES
                (@id, @organizationId, 'Raw Client', @cpf, @personType, @cnpj,
                 @address, @notes, TRUE, @createdAt)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("organizationId", organizationId);
        AddNullableParameter(command, "cpf", NpgsqlDbType.Varchar, cpf);
        AddNullableParameter(command, "personType", NpgsqlDbType.Smallint, personType);
        AddNullableParameter(command, "cnpj", NpgsqlDbType.Varchar, cnpj);
        AddNullableParameter(command, "address", NpgsqlDbType.Varchar, address);
        AddNullableParameter(command, "notes", NpgsqlDbType.Varchar, notes);
        command.Parameters.AddWithValue("createdAt", CreatedAt);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddNullableParameter(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        object? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, type)
        {
            Value = value ?? DBNull.Value
        });
    }

    private async Task<int> CountClientsAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        return await dbContext.Clients.CountAsync();
    }

    private static Organization CreateOrganization(string slug)
    {
        return new Organization(
            $"Client Documents {slug}",
            $"client-documents-{slug}",
            CreatedAt);
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
