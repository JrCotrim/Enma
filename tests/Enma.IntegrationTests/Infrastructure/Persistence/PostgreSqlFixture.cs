using System.Security.Cryptography;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Processes;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

// One database per collection with the D33 identities: the bootstrap
// superuser (ConnectionString, CreateDbContext) migrates, resets, seeds and
// holds test locks; every API host connects as the least-privileged
// application role (AppConnectionString), as in every other environment.
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    public const string MigratorRole = "enma_tests_migrator";
    public const string AppRole = "enma_tests_app";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(TestContainerImages.PostgreSql)
        .WithDatabase("enma_integration_tests")
        .WithUsername("enma_tests")
        .WithPassword("enma_tests_password")
        .WithCreateParameterModifier(LoopbackPortBindings.Apply)
        .Build();

    private readonly string _migratorPassword = CreateSecret();
    private readonly string _appPassword = CreateSecret();
    private DbContextOptions<EnmaDbContext>? _dbContextOptions;

    // Administrative connection: the bootstrap superuser.
    public string ConnectionString => _dbContextOptions is null
        ? throw new InvalidOperationException(
            "The PostgreSQL fixture has not been initialized.")
        : _container.GetConnectionString();

    // The application role: DML only, audit_logs append-only, migration
    // history read-only, no DDL or temporary tables.
    public string AppConnectionString => new NpgsqlConnectionStringBuilder(
        ConnectionString)
    {
        Username = AppRole,
        Password = _appPassword
    }.ConnectionString;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _dbContextOptions = new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;

        await using (EnmaDbContext dbContext = CreateDbContext())
        {
            await dbContext.Database.MigrateAsync();
        }

        await PostgreSqlRoles.ApplyRolesAsync(
            ConnectionString,
            MigratorRole,
            _migratorPassword,
            AppRole,
            _appPassword);
        await ApplyGrantsAsync();
    }

    public EnmaDbContext CreateDbContext()
    {
        DbContextOptions<EnmaDbContext> options = _dbContextOptions
            ?? throw new InvalidOperationException("The PostgreSQL fixture has not been initialized.");

        return new EnmaDbContext(options);
    }

    public static Task InsertClientWithoutProfileColumnsAsync(
        EnmaDbContext dbContext,
        Client client)
    {
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO clients
                (id, organization_id, name, is_active, created_at)
            VALUES
                ({client.Id}, {client.OrganizationId}, {client.Name},
                 {client.IsActive}, {client.CreatedAt})
            """);
    }

    public static Task InsertClientWithoutPersonTypeColumnsAsync(
        EnmaDbContext dbContext,
        Client client)
    {
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO clients
                (id, organization_id, name, email, phone, cpf, is_active,
                 created_at)
            VALUES
                ({client.Id}, {client.OrganizationId}, {client.Name},
                 {client.Email}, {client.Phone}, {client.Cpf}, {client.IsActive},
                 {client.CreatedAt})
            """);
    }

    public static async Task InsertLegalProcessWithoutOperationalColumnsAsync(
        EnmaDbContext dbContext,
        LegalProcess legalProcess)
    {
        int insertedRows = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO legal_processes
                (id, organization_id, client_id, title, created_at)
            SELECT
                {legalProcess.Id}, clients.organization_id, clients.id,
                {legalProcess.Title}, {legalProcess.CreatedAt}
            FROM clients
            WHERE clients.id = {legalProcess.ClientId}
              AND clients.organization_id = {legalProcess.OrganizationId}
            """);

        if (insertedRows != 1)
        {
            throw new InvalidOperationException(
                "The legacy legal process client must belong to the same organization.");
        }
    }

    public static async Task InsertLegalDeadlineWithoutResponsibleColumnAsync(
        EnmaDbContext dbContext,
        LegalDeadline legalDeadline)
    {
        int insertedRows = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO legal_deadlines
                (id, organization_id, process_id, title, due_date, created_at,
                 completed_at)
            SELECT
                {legalDeadline.Id}, legal_processes.organization_id,
                legal_processes.id, {legalDeadline.Title}, {legalDeadline.DueDate},
                {legalDeadline.CreatedAt},
                {legalDeadline.CompletedAt}::timestamp with time zone
            FROM legal_processes
            WHERE legal_processes.id = {legalDeadline.ProcessId}
              AND legal_processes.organization_id = {legalDeadline.OrganizationId}
            """);

        if (insertedRows != 1)
        {
            throw new InvalidOperationException(
                "The legacy legal deadline process must belong to the same organization.");
        }
    }

    // Migration tests move the shared database to older schemas. Every one of
    // them restores the latest schema on dispose, including after a failure,
    // so the following classes of the collection do not fail in cascade. If
    // the upgrade itself fails, the schema is rebuilt from scratch and the
    // original error is still reported for the test that left it behind.
    // Tables the superuser re-creates are converted back to the migrator and
    // granted to the application role, as after every migration (D34).
    public async Task RestoreLatestSchemaAsync()
    {
        try
        {
            await using (EnmaDbContext dbContext = CreateDbContext())
            {
                await dbContext.Database.MigrateAsync();
            }

            await ApplyGrantsAsync();
        }
        catch
        {
            await RecreateLatestSchemaAsync();
            throw;
        }
    }

    // The schema comes back exactly as initdb creates it (owned by
    // pg_database_owner, USAGE for PUBLIC), so the grants find what they
    // expect.
    private async Task RecreateLatestSchemaAsync()
    {
        await using (EnmaDbContext dbContext = CreateDbContext())
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                DROP SCHEMA "public" CASCADE;
                CREATE SCHEMA "public" AUTHORIZATION pg_database_owner;
                GRANT USAGE ON SCHEMA "public" TO PUBLIC;
                """);
            await dbContext.Database.MigrateAsync();
        }

        await ApplyGrantsAsync();
    }

    private Task ApplyGrantsAsync()
    {
        return PostgreSqlRoles.ApplyGrantsAsync(
            ConnectionString,
            MigratorRole,
            AppRole);
    }

    private static string CreateSecret()
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    }

    public async Task ResetDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        await using EnmaDbContext dbContext = CreateDbContext();
        await ResetAuditLogsAsync(dbContext, cancellationToken);
        await dbContext.EmailVerificationSendBudgets.ExecuteDeleteAsync(
            cancellationToken);
        await dbContext.Notifications.ExecuteDeleteAsync(cancellationToken);
        await dbContext.CalendarEvents.ExecuteDeleteAsync(cancellationToken);
        await dbContext.LegalDocuments.ExecuteDeleteAsync(cancellationToken);
        await dbContext.LegalTasks.ExecuteDeleteAsync(cancellationToken);
        await dbContext.LegalDeadlines.ExecuteDeleteAsync(cancellationToken);
        await dbContext.LegalProcesses.ExecuteDeleteAsync(cancellationToken);
        await dbContext.PaymentInstallments.ExecuteDeleteAsync(cancellationToken);
        await dbContext.ClientPaymentPlans.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Clients.ExecuteDeleteAsync(cancellationToken);
        await dbContext.PasswordRecoveryChallenges.ExecuteDeleteAsync(cancellationToken);
        await dbContext.EmailVerificationChallenges.ExecuteDeleteAsync(cancellationToken);
        await dbContext.AuthenticationSessions.ExecuteDeleteAsync(cancellationToken);
        await dbContext.ExternalIdentities.ExecuteDeleteAsync(cancellationToken);
        await dbContext.UserCredentials.ExecuteDeleteAsync(cancellationToken);
        await dbContext.OrganizationInvitations.ExecuteDeleteAsync(cancellationToken);
        await dbContext.OrganizationMemberships.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Users.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Organizations.ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task ResetAuditLogsAsync(
        EnmaDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            ALTER TABLE "public"."audit_logs"
            DISABLE TRIGGER "trg_audit_logs_append_only";
            """,
            cancellationToken);

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                TRUNCATE TABLE "public"."audit_logs";
                """,
                cancellationToken);
        }
        finally
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE "public"."audit_logs"
                ENABLE TRIGGER "trg_audit_logs_append_only";
                """);
        }
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
