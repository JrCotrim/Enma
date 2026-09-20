using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Enma.Api.Health;

internal sealed class PostgreSqlReadinessHealthCheck(
    EnmaDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await dbContext.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy(
                    "PostgreSQL is unavailable.");
            }

            IEnumerable<string> pendingMigrations = await dbContext.Database
                .GetPendingMigrationsAsync(cancellationToken);
            if (pendingMigrations.Any())
            {
                return HealthCheckResult.Unhealthy(
                    "The database schema is not current.");
            }

            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return HealthCheckResult.Unhealthy(
                "PostgreSQL readiness could not be determined.");
        }
    }
}
