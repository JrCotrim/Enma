using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalProcessOperationalMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration =
        "20260923163530_QueueLegalDocumentDeletion";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        9,
        28,
        12,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => fixture.RestoreLatestSchemaAsync();

    [Fact]
    public async Task MigrateAsync_FromPreviousSchema_PreservesLegacyProcessAndAddsDefaults()
    {
        await MigrateAsync(PreviousMigration);
        var organization = new Organization(
            "Legacy Organization",
            "legacy-organization",
            CreatedAt);
        var client = new Client(
            organization.Id,
            "Legacy Client",
            CreatedAt);
        Guid processId = Guid.Parse("7f699a0c-e695-42b4-86bb-06ed6b89a86d");

        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.Add(organization);
            await seedContext.SaveChangesAsync();
            await PostgreSqlFixture.InsertClientWithoutPersonTypeColumnsAsync(
                seedContext,
                client);
            await seedContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO legal_processes
                    (id, organization_id, client_id, title, created_at)
                VALUES
                    ({processId}, {organization.Id}, {client.Id},
                     {"Legacy Process"}, {CreatedAt})
                """);
        }

        await MigrateAsync();

        await using EnmaDbContext assertionContext = fixture.CreateDbContext();
        LegalProcess persisted = await assertionContext.LegalProcesses
            .SingleAsync();
        Assert.Equal(processId, persisted.Id);
        Assert.Equal(organization.Id, persisted.OrganizationId);
        Assert.Equal(client.Id, persisted.ClientId);
        Assert.Equal("Legacy Process", persisted.Title);
        Assert.Equal(CreatedAt, persisted.CreatedAt);
        Assert.Equal(LegalProcessStatus.InProgress, persisted.Status);
        Assert.Null(persisted.ProcessNumber);
        Assert.Null(persisted.NormalizedProcessNumber);
        Assert.Null(persisted.CourtOrAuthority);
        Assert.Null(persisted.ResponsibleMembershipId);
        Assert.False(assertionContext.Database.HasPendingModelChanges());
    }

    private async Task MigrateAsync(string? targetMigration = null)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        IMigrator migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }
}
