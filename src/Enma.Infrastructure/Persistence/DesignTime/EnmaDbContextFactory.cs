using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Enma.Infrastructure.Persistence.DesignTime;

public sealed class EnmaDbContextFactory : IDesignTimeDbContextFactory<EnmaDbContext>
{
    private const string LocalDevelopmentConnectionString =
        "Host=localhost;Port=5432;Database=enma_design;Username=postgres;Password=postgres";

    public EnmaDbContext CreateDbContext(string[] args)
    {
        string? configuredConnectionString = Environment.GetEnvironmentVariable(
            "ENMA_DESIGNTIME_CONNECTION_STRING");
        string connectionString = string.IsNullOrWhiteSpace(configuredConnectionString)
            ? LocalDevelopmentConnectionString
            : configuredConnectionString;

        DbContextOptionsBuilder<EnmaDbContext> optionsBuilder = new();
        optionsBuilder.UseNpgsql(connectionString);

        return new EnmaDbContext(optionsBuilder.Options);
    }
}
