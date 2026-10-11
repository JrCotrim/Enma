using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Enma.Api.ExceptionHandling;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Enma.IntegrationTests.Api.ExceptionHandling;

// Outside Development the Entity Framework failure events are logged at
// Debug; a database exception that nothing handles must still reach the
// global exception handler and be logged at Error.
[Collection(PostgreSqlCollection.Name)]
public sealed class UnhandledDatabaseExceptionLoggingTests(
    PostgreSqlFixture fixture)
{
    private const string MissingRelation = "enma_missing_relation";

    [Fact]
    public async Task UnhandledDatabaseException_OutsideDevelopment_IsLoggedAtErrorByGlobalExceptionHandler()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new EnmaApiFactory(fixture, services =>
        {
            services.AddSingleton<ILoggerProvider>(logs);
            services.ConfigureDbContext<EnmaDbContext>(options =>
                options.AddInterceptors(new MissingRelationInterceptor()));
        });
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { Email = "unknown@example.test", Password = "synthetic-password" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        CapturedLogEntry handled = Assert.Single(
            logs.Entries,
            entry => entry.Category == typeof(GlobalExceptionHandler).FullName);
        Assert.Equal(LogLevel.Error, handled.Level);
        PostgresException databaseException = Assert.IsType<PostgresException>(
            FindDatabaseException(handled.Exception));
        Assert.Equal(PostgresErrorCodes.UndefinedTable, databaseException.SqlState);
        Assert.DoesNotContain(
            logs.Entries,
            entry => entry.Category.StartsWith(
                    "Microsoft.EntityFrameworkCore",
                    StringComparison.Ordinal) &&
                entry.Level >= LogLevel.Error);
    }

    private static DbException? FindDatabaseException(Exception? exception)
    {
        for (Exception? current = exception;
            current is not null;
            current = current.InnerException)
        {
            if (current is DbException databaseException)
            {
                return databaseException;
            }
        }

        return null;
    }

    // Turns every query into a real PostgreSQL failure (42P01) that no
    // application code translates.
    private sealed class MissingRelationInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            command.CommandText = $"SELECT 1 FROM {MissingRelation}";
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            command.CommandText = $"SELECT 1 FROM {MissingRelation}";
            return ValueTask.FromResult(result);
        }
    }
}
