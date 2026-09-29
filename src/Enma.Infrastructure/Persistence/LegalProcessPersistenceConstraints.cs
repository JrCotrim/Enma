using Npgsql;

namespace Enma.Infrastructure.Persistence;

internal static class LegalProcessPersistenceConstraints
{
    public const string NormalizedProcessNumber =
        "ux_legal_processes_organization_id_normalized_process_number";

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
