namespace Enma.Api.Contracts.Processes;

public sealed class CreateLegalProcessRequest
{
    public required Guid ClientId { get; init; }

    public required string Title { get; init; }
}
