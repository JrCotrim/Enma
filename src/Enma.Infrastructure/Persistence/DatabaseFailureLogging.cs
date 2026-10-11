using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Enma.Infrastructure.Persistence;

// EF Core reports every failed command at Error before the persistence layer
// can translate an expected conflict (lock not available, unique violation)
// into a result. Outside Development these events are lowered to Debug; a
// database exception that escapes is still logged at Error by the API's
// global exception handler.
public static class DatabaseFailureLogging
{
    public static DbContextOptionsBuilder LogDatabaseFailureEventsAtDebug(
        this DbContextOptionsBuilder options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.ConfigureWarnings(warnings => warnings.Log(
            (RelationalEventId.CommandError, LogLevel.Debug),
            (CoreEventId.QueryIterationFailed, LogLevel.Debug),
            (CoreEventId.SaveChangesFailed, LogLevel.Debug)));
    }
}
