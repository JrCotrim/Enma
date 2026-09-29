namespace Enma.Api.Contracts.Processes;

public sealed class ChangeLegalProcessStatusRequest
{
    public required string Status { get; init; }
}
