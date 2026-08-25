using Enma.Application.Authorization;
using Enma.Application.Validation;
using Enma.Domain.Organizations;

namespace Enma.Application.Organizations.Members.Role;

public sealed class ChangeOrganizationMemberRoleUseCase
{
    private readonly OrganizationAdministrationAuthorization _authorization;
    private readonly IOrganizationMemberRoleMutationPersistence _persistence;

    public ChangeOrganizationMemberRoleUseCase(
        OrganizationAdministrationAuthorization authorization,
        IOrganizationMemberRoleMutationPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(persistence);

        _authorization = authorization;
        _persistence = persistence;
    }

    public async Task<ChangeOrganizationMemberRoleResult> ExecuteAsync(
        ChangeOrganizationMemberRoleCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        OrganizationRole role = ParseMutableRole(command.Role, "Role");
        OrganizationRole expectedCurrentRole = ParseMutableRole(
            command.ExpectedCurrentRole,
            "Expected current role");

        OrganizationAdministrationAuthorizationResult authorization =
            await _authorization.AuthorizeAsync(
                command.UserId,
                command.OrganizationId,
                cancellationToken);

        if (!authorization.Allows(
                OrganizationAdministrationAction.ChangeMemberRole) ||
            authorization.UserId != command.UserId ||
            authorization.OrganizationId != command.OrganizationId ||
            authorization.MembershipId is not Guid actorMembershipId ||
            actorMembershipId == Guid.Empty)
        {
            return ChangeOrganizationMemberRoleResult.AccessDenied;
        }

        if (command.MembershipId == Guid.Empty)
        {
            return ChangeOrganizationMemberRoleResult.NotFound;
        }

        var request = new OrganizationMemberRoleMutationPersistenceRequest(
            command.UserId,
            command.OrganizationId,
            actorMembershipId,
            command.MembershipId,
            role,
            expectedCurrentRole);
        OrganizationMemberRoleMutationPersistenceResult persistenceResult =
            await _persistence.ExecuteAsync(request, cancellationToken);

        return persistenceResult switch
        {
            OrganizationMemberRoleMutationPersistenceResult.AccessDenied =>
                ChangeOrganizationMemberRoleResult.AccessDenied,
            OrganizationMemberRoleMutationPersistenceResult.NotFound =>
                ChangeOrganizationMemberRoleResult.NotFound,
            OrganizationMemberRoleMutationPersistenceResult.TargetForbidden =>
                ChangeOrganizationMemberRoleResult.TargetForbidden,
            OrganizationMemberRoleMutationPersistenceResult.Conflict =>
                ChangeOrganizationMemberRoleResult.Conflict,
            OrganizationMemberRoleMutationPersistenceResult.Succeeded =>
                ChangeOrganizationMemberRoleResult.Succeeded,
            OrganizationMemberRoleMutationPersistenceResult.InvalidInput =>
                throw new InvalidOperationException(
                    "Validated organization role mutation input was rejected by persistence."),
            _ => throw new InvalidOperationException(
                "Organization role mutation persistence returned an invalid result.")
        };
    }

    private static OrganizationRole ParseMutableRole(
        string? value,
        string fieldName)
    {
        return value switch
        {
            "Administrator" => OrganizationRole.Administrator,
            "Member" => OrganizationRole.Member,
            _ => throw new RequestValidationException(
                $"{fieldName} must be either 'Administrator' or 'Member'.")
        };
    }
}
