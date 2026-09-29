using Enma.Domain.Processes;

namespace Enma.Infrastructure.Persistence.Queries;

internal sealed record LegalProcessSearchPattern(
    string ContainsPattern,
    string? DigitsContainsPattern)
{
    public const string LikeEscapeCharacter = "\\";

    public static LegalProcessSearchPattern? Create(string? search)
    {
        if (search is null)
        {
            return null;
        }

        string? digits = LegalProcess.ToProcessNumberDigitsSearchTerm(search);

        return new LegalProcessSearchPattern(
            $"%{EscapeLikePattern(search)}%",
            digits is null ? null : $"%{EscapeLikePattern(digits)}%");
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
