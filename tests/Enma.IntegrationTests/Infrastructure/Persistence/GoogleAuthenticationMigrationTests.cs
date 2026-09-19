using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class GoogleAuthenticationMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration =
        "20260915151226_AddPasswordRecoveryChallenges";
    private static readonly DateTimeOffset Now =
        new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => MigrateAsync();

    [Fact]
    public async Task MigrateAsync_UpAndDown_PreservesLocalCredentialsAndSchema()
    {
        await MigrateAsync(PreviousMigration);
        var user = new User("Local User", "local@example.test", Now);
        var credential = new UserCredential(user.Id, "existing-hash", Now);
        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.AddRange(user, credential);
            await seedContext.SaveChangesAsync();
        }

        await MigrateAsync();

        Assert.Equal("YES", await GetPasswordHashNullabilityAsync());
        Assert.True(await ExternalIdentityTableExistsAsync());
        Assert.Equal(
            [
                "pk_external_identities",
                "ux_external_identities_provider_subject",
                "ux_external_identities_user_provider"
            ],
            await GetExternalIdentityIndexesAsync());
        Assert.True(await HasCascadeForeignKeyAsync());
        await using (EnmaDbContext upgradedContext = fixture.CreateDbContext())
        {
            Assert.Equal(
                "existing-hash",
                (await upgradedContext.UserCredentials.AsNoTracking().SingleAsync())
                    .PasswordHash);
        }

        await MigrateAsync(PreviousMigration);

        Assert.Equal("NO", await GetPasswordHashNullabilityAsync());
        Assert.False(await ExternalIdentityTableExistsAsync());
        await using EnmaDbContext downgradedContext = fixture.CreateDbContext();
        Assert.Equal(
            "existing-hash",
            (await downgradedContext.UserCredentials.AsNoTracking().SingleAsync())
                .PasswordHash);
    }

    [Fact]
    public async Task MigrateAsync_DownWithPasswordlessUser_FailsWithoutDataLoss()
    {
        var user = new User("Google User", "google@example.test", Now);
        user.VerifyEmail(Now);
        var credential = new UserCredential(user.Id, passwordHash: null, Now);
        var identity = new ExternalIdentity(user.Id, "Google", "subject", Now);
        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.AddRange(user, credential, identity);
            await seedContext.SaveChangesAsync();
        }

        await Assert.ThrowsAnyAsync<Exception>(
            () => MigrateAsync(PreviousMigration));

        Assert.Equal("YES", await GetPasswordHashNullabilityAsync());
        Assert.True(await ExternalIdentityTableExistsAsync());
        await using EnmaDbContext assertionContext = fixture.CreateDbContext();
        Assert.Null((await assertionContext.UserCredentials
            .AsNoTracking()
            .SingleAsync()).PasswordHash);
        Assert.Equal(identity.Id, (await assertionContext.ExternalIdentities
            .AsNoTracking()
            .SingleAsync()).Id);
    }

    private async Task MigrateAsync(string? targetMigration = null)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        IMigrator migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }

    private async Task<string> GetPasswordHashNullabilityAsync()
    {
        const string Query =
            """
            SELECT is_nullable
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'user_credentials'
              AND column_name = 'password_hash'
            """;
        return (string)(await ExecuteScalarAsync(Query))!;
    }

    private async Task<bool> ExternalIdentityTableExistsAsync()
    {
        const string Query =
            "SELECT to_regclass('public.external_identities') IS NOT NULL";
        return (bool)(await ExecuteScalarAsync(Query))!;
    }

    private async Task<string[]> GetExternalIdentityIndexesAsync()
    {
        const string Query =
            """
            SELECT indexname
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'external_identities'
            ORDER BY indexname
            """;
        var names = new List<string>();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private async Task<bool> HasCascadeForeignKeyAsync()
    {
        const string Query =
            """
            SELECT confdeltype = 'c'
            FROM pg_constraint
            WHERE conname = 'fk_external_identities_users_user_id'
            """;
        return (bool)(await ExecuteScalarAsync(Query))!;
    }

    private async Task<object?> ExecuteScalarAsync(string query)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(query, connection);
        return await command.ExecuteScalarAsync();
    }
}
