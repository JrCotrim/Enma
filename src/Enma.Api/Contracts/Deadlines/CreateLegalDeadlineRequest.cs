namespace Enma.Api.Contracts.Deadlines;

public sealed class CreateLegalDeadlineRequest
{
    public required Guid ProcessId { get; init; }

    public required string Title { get; init; }

    public required DateOnly DueDate { get; init; }
}
