using Enma.Application.Authorization;
using Enma.Application.Validation;
using Enma.Domain.Organizations;

namespace Enma.Application.Organizations.Members.Ownership;

public sealed class TransferOrganizationOwnershipUseCase
{
    private readonly OrganizationAdministrationAuthorization _authorization;
    private readonly IOrganizationOwnershipTransferPersistence _persistence;

    public TransferOrganizationOwnershipUseCase(
        OrganizationAdministrationAuthorization authorization,
        IOrganizationOwnershipTransferPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(persistence);

        _authorization = authorization;
        _persistence = persistence;
    }

    public async Task<TransferOrganizationOwnershipResult> ExecuteAsync(
        TransferOrganizationOwnershipCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        OrganizationRole expectedTargetRole = ParseExpectedTargetRole(
            command.ExpectedTargetRole);

        // Precheck only: the persistence transaction revalidates the live actor role.
        OrganizationAdministrationAuthorizationResult authorization =
            await _authorization.AuthorizeAsync(
                command.UserId,
                command.OrganizationId,
                cancellationToken);

        if (!authorization.Allows(
                OrganizationAdministrationAction.TransferOwnership) ||
            authorization.UserId != command.UserId ||
            authorization.OrganizationId != command.OrganizationId ||
            authorization.MembershipId is not Guid actorMembershipId ||
            actorMembershipId == Guid.Empty)
        {
            return TransferOrganizationOwnershipResult.AccessDenied;
        }

        if (command.MembershipId == Guid.Empty)
        {
            return TransferOrganizationOwnershipResult.NotFound;
        }

        var request = new OrganizationOwnershipTransferPersistenceRequest(
            command.UserId,
            command.OrganizationId,
            actorMembershipId,
            command.MembershipId,
            expectedTargetRole);
        OrganizationOwnershipTransferPersistenceResult persistenceResult =
            await _persistence.ExecuteAsync(request, cancellationToken);

        return persistenceResult switch
        {
            OrganizationOwnershipTransferPersistenceResult.AccessDenied =>
                TransferOrganizationOwnershipResult.AccessDenied,
            OrganizationOwnershipTransferPersistenceResult.NotFound =>
                TransferOrganizationOwnershipResult.NotFound,
            OrganizationOwnershipTransferPersistenceResult.TargetUnavailable =>
                TransferOrganizationOwnershipResult.TargetUnavailable,
            OrganizationOwnershipTransferPersistenceResult.Succeeded =>
                TransferOrganizationOwnershipResult.Succeeded,
            OrganizationOwnershipTransferPersistenceResult.InvalidInput =>
                throw new InvalidOperationException(
                    "Validated ownership transfer input was rejected by persistence."),
            _ => throw new InvalidOperationException(
                "Ownership transfer persistence returned an invalid result.")
        };
    }

    // Only roles a transfer target can legitimately hold are accepted. Any value
    // other than the live Administrator role is rejected later as a conflict.
    private static OrganizationRole ParseExpectedTargetRole(string? value)
    {
        return value switch
        {
            "administrator" => OrganizationRole.Administrator,
            "member" => OrganizationRole.Member,
            _ => throw new RequestValidationException(
                "Expected target role must be either 'administrator' or 'member'.")
        };
    }
}
