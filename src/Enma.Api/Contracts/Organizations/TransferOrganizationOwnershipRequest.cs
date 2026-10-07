namespace Enma.Api.Contracts.Organizations;

public sealed class TransferOrganizationOwnershipRequest
{
    public required string ExpectedTargetRole { get; init; }
}
