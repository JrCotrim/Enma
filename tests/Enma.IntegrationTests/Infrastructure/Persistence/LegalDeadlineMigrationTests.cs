using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDeadlineMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration =
        "20260812210241_AddLegalProcesses";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        13,
        18,
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
        (Organization organization, Client client, LegalProcess legalProcess) =
            CreateProcessGraph();
        var legalDeadline = new LegalDeadline(
            organization.Id,
            legalProcess.Id,
            "Fresh Schema Deadline",
            new DateOnly(2026, 11, 1),
            CreatedAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(organization, client, legalProcess, legalDeadline);
        await dbContext.SaveChangesAsync();

        Assert.Equal(1, await dbContext.LegalDeadlines.CountAsync());
    }

    [Fact]
    public async Task MigrateAsync_FromAddLegalProcesses_PreservesDataAndCreatesUsableSchema()
    {
        await MigrateAsync(PreviousMigration);
        (Organization organization, Client client, LegalProcess legalProcess) =
            CreateProcessGraph();

        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.Add(organization);
            await seedContext.SaveChangesAsync();
            await PostgreSqlFixture.InsertClientWithoutProfileColumnsAsync(
                seedContext,
                client);
            seedContext.Add(legalProcess);
            await seedContext.SaveChangesAsync();
        }

        await MigrateAsync();

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization persistedOrganization = await dbContext.Organizations.SingleAsync();
        Client persistedClient = await dbContext.Clients.SingleAsync();
        LegalProcess persistedLegalProcess =
            await dbContext.LegalProcesses.SingleAsync();
        var legalDeadline = new LegalDeadline(
            persistedOrganization.Id,
            persistedLegalProcess.Id,
            "Upgraded Schema Deadline",
            new DateOnly(2026, 11, 1),
            CreatedAt.AddMinutes(1));
        dbContext.LegalDeadlines.Add(legalDeadline);
        await dbContext.SaveChangesAsync();

        Assert.Equal(organization.Id, persistedOrganization.Id);
        Assert.Equal(client.Id, persistedClient.Id);
        Assert.Equal("Existing Client", persistedClient.Name);
        Assert.Equal(legalProcess.Id, persistedLegalProcess.Id);
        Assert.Equal("Existing Process", persistedLegalProcess.Title);
        Assert.Equal(1, await dbContext.LegalDeadlines.CountAsync());
    }

    private async Task MigrateAsync(string? targetMigration = null)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        IMigrator migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private static (Organization, Client, LegalProcess) CreateProcessGraph()
    {
        var organization = new Organization(
            "Enma Legal",
            "enma-legal",
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

        return (organization, client, legalProcess);
    }
}
