namespace Enma.Infrastructure.Time;

public sealed class OperationalTimeZoneOptions
{
    public const string SectionName = "OperationalTimeZone";

    public const string DefaultTimeZoneId = "America/Sao_Paulo";

    public string TimeZoneId { get; init; } = DefaultTimeZoneId;
}
