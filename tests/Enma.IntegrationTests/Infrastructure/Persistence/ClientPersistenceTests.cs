using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class ClientPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        11,
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
    public async Task SaveChangesAsync_WithValidClient_PersistsTenantOwnedClient()
    {
        Organization organization = CreateOrganization();
        var client = new Client(
            organization.Id,
            "Acme Legal",
            CreatedAt,
            "client@example.test",
            "11987654321",
            "52998224725");
        var clientWithoutProfile = new Client(
            organization.Id,
            "No Profile",
            CreatedAt.AddMinutes(1));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(organization, client, clientWithoutProfile);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        Client persistedClient = await dbContext.Clients.SingleAsync(
            candidate => candidate.Id == client.Id);
        Client persistedClientWithoutProfile = await dbContext.Clients.SingleAsync(
            candidate => candidate.Id == clientWithoutProfile.Id);

        Assert.Equal(client.Id, persistedClient.Id);
        Assert.Equal(organization.Id, persistedClient.OrganizationId);
        Assert.Equal("Acme Legal", persistedClient.Name);
        Assert.Equal("client@example.test", persistedClient.Email);
        Assert.Equal("11987654321", persistedClient.Phone);
        Assert.Equal("52998224725", persistedClient.Cpf);
        Assert.True(persistedClient.IsActive);
        Assert.Equal(CreatedAt, persistedClient.CreatedAt);
        Assert.Null(persistedClientWithoutProfile.Email);
        Assert.Null(persistedClientWithoutProfile.Phone);
        Assert.Null(persistedClientWithoutProfile.Cpf);
    }

    [Fact]
    public async Task SaveChangesAsync_WithMissingOrganization_EnforcesForeignKey()
    {
        var client = new Client(Guid.NewGuid(), "Acme Legal", CreatedAt);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Clients.Add(client);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            "fk_clients_organizations_organization_id");
    }

    [Fact]
    public async Task DatabaseInsert_WithNullOrganizationId_EnforcesNotNull()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO clients (id, organization_id, name, is_active, created_at)
                VALUES ({Guid.NewGuid()}, NULL, {"Acme Legal"}, {true}, {CreatedAt})
                """));

        Assert.Equal(PostgresErrorCodes.NotNullViolation, exception.SqlState);
        Assert.Equal("organization_id", exception.ColumnName);
    }

    [Fact]
    public async Task DatabaseInsert_WithNullName_EnforcesNotNull()
    {
        Organization organization = CreateOrganization();
        await SeedAsync(organization);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO clients (id, organization_id, name, is_active, created_at)
                VALUES ({Guid.NewGuid()}, {organization.Id}, NULL, {true}, {CreatedAt})
                """));

        Assert.Equal(PostgresErrorCodes.NotNullViolation, exception.SqlState);
        Assert.Equal("name", exception.ColumnName);
    }

    [Fact]
    public async Task DatabaseInsert_WithNameBeyondMaximumLength_EnforcesVarcharLimit()
    {
        Organization organization = CreateOrganization();
        await SeedAsync(organization);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        string name = new('a', 151);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO clients (id, organization_id, name, is_active, created_at)
                VALUES ({Guid.NewGuid()}, {organization.Id}, {name}, {true}, {CreatedAt})
                """));

        Assert.Equal(
            PostgresErrorCodes.StringDataRightTruncation,
            exception.SqlState);
    }

    [Fact]
    public async Task SaveChangesAsync_WithDuplicateNamesInOrganization_AllowsBothClients()
    {
        Organization organization = CreateOrganization();
        var firstClient = new Client(organization.Id, "Shared Name", CreatedAt);
        var secondClient = new Client(
            organization.Id,
            "Shared Name",
            CreatedAt.AddMinutes(1));
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(organization, firstClient, secondClient);

        await dbContext.SaveChangesAsync();

        Assert.Equal(2, await dbContext.Clients.CountAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_WhenDeletingOwningOrganization_RestrictsDelete()
    {
        Organization organization = CreateOrganization();
        var client = new Client(organization.Id, "Acme Legal", CreatedAt);
        await SeedAsync(organization, client);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization persistedOrganization = await dbContext.Organizations.SingleAsync();
        dbContext.Organizations.Remove(persistedOrganization);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.RestrictViolation,
            "fk_clients_organizations_organization_id");
    }

    [Fact]
    public void ClientModel_WithTenantOwnership_HasExpectedConstraintsAndIndexes()
    {
        using EnmaDbContext dbContext = fixture.CreateDbContext();
        IEntityType? entityType = dbContext.Model.FindEntityType(typeof(Client));

        Assert.NotNull(entityType);
        Assert.Equal("clients", entityType.GetTableName());
        Assert.False(entityType.FindProperty(nameof(Client.OrganizationId))!.IsNullable);
        Assert.False(entityType.FindProperty(nameof(Client.Name))!.IsNullable);
        Assert.Equal(
            150,
            entityType.FindProperty(nameof(Client.Name))!.GetMaxLength());

        IKey alternateKey = Assert.Single(
            entityType.GetKeys(),
            key => !key.IsPrimaryKey());
        Assert.Equal(
            "ak_clients_organization_id_id",
            alternateKey.GetName());
        Assert.Equal(
            [nameof(Client.OrganizationId), nameof(Client.Id)],
            alternateKey.Properties.Select(property => property.Name).ToArray());
        Assert.Empty(entityType.GetIndexes());
        Assert.DoesNotContain(
            entityType.GetIndexes(),
            candidate => candidate.Properties.Any(
                property => property.Name == nameof(Client.Name)));

        IForeignKey foreignKey = Assert.Single(entityType.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.Equal(typeof(Organization), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(
            [nameof(Client.OrganizationId)],
            foreignKey.Properties.Select(property => property.Name).ToArray());
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static Organization CreateOrganization()
    {
        return new Organization("Enma Legal", "enma-legal", CreatedAt);
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
