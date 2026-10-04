using Npgsql;

namespace Enma.Infrastructure.Persistence;

internal static class PostgreSqlConstraintViolations
{
    public static bool IsUniqueViolation(
        Exception exception,
        string constraintName)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation
                } postgresException &&
                string.Equals(
                    postgresException.ConstraintName,
                    constraintName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
