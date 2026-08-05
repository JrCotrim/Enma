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

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
