using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalProcessMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration =
        "20260811200710_AddClients";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        12,
        16,
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
    public async Task MigrateAsync_FromEmptyDatabase_CreatesUsableLatestSchema()
    {
        await MigrateAsync(Migration.InitialDatabase);
        await MigrateAsync();

        Organization organization = CreateOrganization();
        var client = new Client(organization.Id, "Existing Client", CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            "Fresh Schema Process",
            CreatedAt);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(organization, client, legalProcess);
        await dbContext.SaveChangesAsync();

        Assert.Equal(1, await dbContext.LegalProcesses.CountAsync());
    }

    [Fact]
    public async Task MigrateAsync_FromAddClients_PreservesClientAndCreatesUsableSchema()
    {
        await MigrateAsync(PreviousMigration);
        Organization organization = CreateOrganization();
        var client = new Client(organization.Id, "Existing Client", CreatedAt);

        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.AddRange(organization, client);
            await seedContext.SaveChangesAsync();
        }

        await MigrateAsync();

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Client persistedClient = await dbContext.Clients.SingleAsync();
        var legalProcess = new LegalProcess(
            persistedClient.OrganizationId,
            persistedClient.Id,
            "Upgraded Schema Process",
            CreatedAt.AddMinutes(1));
        dbContext.LegalProcesses.Add(legalProcess);
        await dbContext.SaveChangesAsync();

        Assert.Equal(client.Id, persistedClient.Id);
        Assert.Equal("Existing Client", persistedClient.Name);
        Assert.Equal(1, await dbContext.LegalProcesses.CountAsync());
    }

    private async Task MigrateAsync(string? targetMigration = null)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        IMigrator migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private static Organization CreateOrganization()
    {
        return new Organization("Enma Legal", "enma-legal", CreatedAt);
    }
}
