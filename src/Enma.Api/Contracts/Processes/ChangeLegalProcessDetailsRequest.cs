namespace Enma.Api.Contracts.Processes;

public sealed class ChangeLegalProcessDetailsRequest
{
    public required string? ProcessNumber { get; init; }

    public required string? CourtOrAuthority { get; init; }
}
