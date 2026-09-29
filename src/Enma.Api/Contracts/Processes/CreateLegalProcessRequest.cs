namespace Enma.Api.Contracts.Processes;

public sealed class CreateLegalProcessRequest
{
    public required Guid ClientId { get; init; }

    public required string Title { get; init; }

    public string? ProcessNumber { get; init; }

    public string? Status { get; init; }

    public string? CourtOrAuthority { get; init; }

    public Guid? ResponsibleMembershipId { get; init; }
}
