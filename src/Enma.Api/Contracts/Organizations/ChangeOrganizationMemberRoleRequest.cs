namespace Enma.Api.Contracts.Organizations;

public sealed class ChangeOrganizationMemberRoleRequest
{
    public required string Role { get; init; }

    public required string ExpectedCurrentRole { get; init; }
}
