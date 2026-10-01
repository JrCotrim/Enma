using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Processes;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("enma_integration_tests")
        .WithUsername("enma_tests")
        .WithPassword("enma_tests_password")
        .Build();

    private DbContextOptions<EnmaDbContext>? _dbContextOptions;

    public string ConnectionString => _dbContextOptions is null
        ? throw new InvalidOperationException(
            "The PostgreSQL fixture has not been initialized.")
        : _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _dbContextOptions = new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;

        await using EnmaDbContext dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
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
