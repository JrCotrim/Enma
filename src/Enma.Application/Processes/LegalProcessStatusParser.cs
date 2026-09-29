using Enma.Application.Validation;
using Enma.Domain.Processes;

namespace Enma.Application.Processes;

internal static class LegalProcessStatusParser
{
    public static LegalProcessStatus Parse(string? value)
    {
        return value switch
        {
            "inProgress" => LegalProcessStatus.InProgress,
            "suspended" => LegalProcessStatus.Suspended,
            "closed" => LegalProcessStatus.Closed,
            _ => throw new RequestValidationException(
                "Status must be 'inProgress', 'suspended', or 'closed'.")
        };
    }
}
