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

    public async Task ResetDatabaseAsync(
        CancellationToken cancellationToken = default)
    {
        await using EnmaDbContext dbContext = CreateDbContext();
        await dbContext.EmailVerificationSendBudgets.ExecuteDeleteAsync(
            cancellationToken);
        await dbContext.Notifications.ExecuteDeleteAsync(cancellationToken);
        await dbContext.CalendarEvents.ExecuteDeleteAsync(cancellationToken);
        await dbContext.LegalDocuments.ExecuteDeleteAsync(cancellationToken);
        await dbContext.LegalTasks.ExecuteDeleteAsync(cancellationToken);
        await dbContext.LegalDeadlines.ExecuteDeleteAsync(cancellationToken);
        await dbContext.LegalProcesses.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Clients.ExecuteDeleteAsync(cancellationToken);
        await dbContext.EmailVerificationChallenges.ExecuteDeleteAsync(cancellationToken);
        await dbContext.AuthenticationSessions.ExecuteDeleteAsync(cancellationToken);
        await dbContext.UserCredentials.ExecuteDeleteAsync(cancellationToken);
        await dbContext.OrganizationMemberships.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Users.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Organizations.ExecuteDeleteAsync(cancellationToken);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
