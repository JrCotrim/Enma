using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class PasswordRecoveryMigrationTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string PreviousMigration = "20260913161005_AddNotificationDismissal";
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MigrationFromPreviousSchemaPreservesCredentialsAndCreatesOnlyRecoveryTable()
    {
        await MigrateAsync(PreviousMigration);
        var user = new User(
            "Migration Recovery User",
            "migration-recovery@example.test",
            CreatedAt);
        var credential = new UserCredential(
            user.Id,
            new AspNetCorePasswordHasher(new PasswordHasher<object>())
                .HashPassword("Migration-Synthetic-Password-123!"),
            CreatedAt);
        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.AddRange(user, credential);
            await seedContext.SaveChangesAsync();
        }
        string[] tablesBefore = await GetTablesAsync();
        Assert.DoesNotContain("password_recovery_challenges", tablesBefore);

        await MigrateAsync();

        Assert.Equal(
            tablesBefore.Append("password_recovery_challenges").Order(StringComparer.Ordinal),
            await GetTablesAsync());
        await using EnmaDbContext assertionContext = fixture.CreateDbContext();
        Assert.Equal(user.Id, (await assertionContext.Users.SingleAsync()).Id);
        Assert.Equal(user.Id, (await assertionContext.UserCredentials.SingleAsync()).UserId);
        Assert.False(assertionContext.Database.HasPendingModelChanges());
        Assert.Equal(
            [
                "created_at|timestamp with time zone|NO|",
                "email_at_issue|character varying|NO|254",
                "expires_at|timestamp with time zone|NO|",
                "token_hash|bytea|NO|",
                "user_id|uuid|NO|"
            ],
            await GetColumnsAsync());
        Assert.Equal(
            [
                "ck_password_recovery_challenges_expiration",
                "ck_password_recovery_challenges_token_hash_length",
                "fk_password_recovery_challenges_users_user_id",
                "pk_password_recovery_challenges"
            ],
            await GetConstraintsAsync());
        Assert.Equal(
            [
                "ix_password_recovery_challenges_expires_at",
                "pk_password_recovery_challenges",
                "ux_password_recovery_challenges_token_hash"
            ],
            await GetIndexesAsync());

        await MigrateAsync(PreviousMigration);
        Assert.DoesNotContain("password_recovery_challenges", await GetTablesAsync());
        await MigrateAsync();
        Assert.Contains("password_recovery_challenges", await GetTablesAsync());
    }

    private async Task MigrateAsync(string? targetMigration = null)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        await dbContext.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private async Task<string[]> GetTablesAsync() => await ReadSingleColumnAsync(
        "SELECT tablename FROM pg_tables WHERE schemaname = 'public' " +
        "AND tablename <> '__EFMigrationsHistory' ORDER BY tablename");

    private async Task<string[]> GetConstraintsAsync() => await ReadSingleColumnAsync(
        "SELECT conname FROM pg_constraint WHERE conrelid = " +
        "'public.password_recovery_challenges'::regclass " +
        "AND contype IN ('p', 'f', 'c') ORDER BY conname");

    private async Task<string[]> GetIndexesAsync() => await ReadSingleColumnAsync(
        "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' " +
        "AND tablename = 'password_recovery_challenges' ORDER BY indexname");

    private async Task<string[]> GetColumnsAsync()
    {
        const string sql =
            "SELECT column_name, data_type, is_nullable, character_maximum_length " +
            "FROM information_schema.columns WHERE table_schema = 'public' " +
            "AND table_name = 'password_recovery_challenges' ORDER BY column_name";
        var values = new List<string>();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}|" +
                $"{(reader.IsDBNull(3) ? "" : reader.GetInt32(3))}");
        }
        return values.ToArray();
    }

    private async Task<string[]> ReadSingleColumnAsync(string sql)
    {
        var values = new List<string>();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values.ToArray();
    }
}
