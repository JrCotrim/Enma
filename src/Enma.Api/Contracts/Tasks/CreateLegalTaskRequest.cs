namespace Enma.Api.Contracts.Tasks;

public sealed class CreateLegalTaskRequest
{
    public required string Title { get; init; }

    public string? Description { get; init; }

    public DateOnly? DueDate { get; init; }

    public Guid? ProcessId { get; init; }

    public Guid? AssigneeMembershipId { get; init; }
}
