namespace Enma.Api.Contracts.Deadlines;

public sealed class UpdateLegalDeadlineRequest
{
    public required string Title { get; init; }

    public required DateOnly DueDate { get; init; }
}
