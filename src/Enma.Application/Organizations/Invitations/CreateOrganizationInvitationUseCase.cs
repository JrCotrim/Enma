using Enma.Application.Authorization;
using Enma.Application.Validation;
using Enma.Domain.Organizations;
using Enma.Domain.Users;

namespace Enma.Application.Organizations.Invitations;

public sealed class CreateOrganizationInvitationUseCase
{
    private readonly OrganizationAdministrationAuthorization authorization;
    private readonly IOrganizationInvitationMutationPersistence persistence;
    private readonly IOrganizationInvitationDelivery delivery;

    public CreateOrganizationInvitationUseCase(
        OrganizationAdministrationAuthorization authorization,
        IOrganizationInvitationMutationPersistence persistence,
        IOrganizationInvitationDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(delivery);

        this.authorization = authorization;
        this.persistence = persistence;
        this.delivery = delivery;
    }

    public async Task<CreateOrganizationInvitationResult> ExecuteAsync(
        CreateOrganizationInvitationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        string email = NormalizeEmail(command.Email);
        OrganizationRole role = ParseInvitableRole(command.Role);
        OrganizationAdministrationAuthorizationResult authorizationResult =
            await authorization.AuthorizeAsync(
                command.UserId,
                command.OrganizationId,
                cancellationToken);

        if (!authorizationResult.Allows(
                OrganizationAdministrationAction.CreateInvitation,
                role) ||
            authorizationResult.UserId != command.UserId ||
            authorizationResult.OrganizationId != command.OrganizationId ||
            authorizationResult.MembershipId is not Guid actorMembershipId ||
            actorMembershipId == Guid.Empty)
        {
            return new CreateOrganizationInvitationResult(
                CreateOrganizationInvitationStatus.AccessDenied);
        }

        CreateOrganizationInvitationPersistenceResult persistenceResult =
            await persistence.CreateAsync(
                new CreateOrganizationInvitationPersistenceRequest(
                    command.UserId,
                    command.OrganizationId,
                    actorMembershipId,
                    email,
                    role),
                cancellationToken);

        if (persistenceResult.Status !=
            CreateOrganizationInvitationPersistenceStatus.Succeeded)
        {
            return new CreateOrganizationInvitationResult(
                MapStatus(persistenceResult.Status));
        }

        OrganizationInvitationDeliveryRequest deliveryRequest =
            persistenceResult.DeliveryRequest
            ?? throw new InvalidOperationException(
                "Successful invitation creation must include delivery data.");
        OrganizationInvitationDeliveryResult deliveryResult =
            await delivery.DeliverAsync(deliveryRequest, cancellationToken);

        return new CreateOrganizationInvitationResult(
            CreateOrganizationInvitationStatus.Succeeded,
            persistenceResult.InvitationId,
            deliveryResult);
    }

    private static string NormalizeEmail(string? email)
    {
        try
        {
            return User.NormalizeEmail(email ?? string.Empty);
        }
        catch (ArgumentException exception)
        {
            throw new RequestValidationException(exception.Message);
        }
    }

    private static OrganizationRole ParseInvitableRole(string? role)
    {
        return role switch
        {
            "Administrator" => OrganizationRole.Administrator,
            "Member" => OrganizationRole.Member,
            _ => throw new RequestValidationException(
                "Role must be either 'Administrator' or 'Member'.")
        };
    }

    private static CreateOrganizationInvitationStatus MapStatus(
        CreateOrganizationInvitationPersistenceStatus status)
    {
        return status switch
        {
            CreateOrganizationInvitationPersistenceStatus.AccessDenied =>
                CreateOrganizationInvitationStatus.AccessDenied,
            CreateOrganizationInvitationPersistenceStatus
                .ExistingActiveMembership =>
                CreateOrganizationInvitationStatus.ExistingActiveMembership,
            CreateOrganizationInvitationPersistenceStatus
                .IncompatibleInactiveMembership =>
                CreateOrganizationInvitationStatus.IncompatibleInactiveMembership,
            CreateOrganizationInvitationPersistenceStatus
                .DuplicatePendingInvitation =>
                CreateOrganizationInvitationStatus.DuplicatePendingInvitation,
            CreateOrganizationInvitationPersistenceStatus.InvalidInput =>
                throw new InvalidOperationException(
                    "Validated invitation creation input was rejected by persistence."),
            _ => throw new InvalidOperationException(
                "Invitation creation persistence returned an invalid result.")
        };
    }
}

public sealed record CreateOrganizationInvitationCommand(
    Guid UserId,
    Guid OrganizationId,
    string? Email,
    string? Role);

public sealed record CreateOrganizationInvitationResult(
    CreateOrganizationInvitationStatus Status,
    Guid? InvitationId = null,
    OrganizationInvitationDeliveryResult? DeliveryStatus = null);

public enum CreateOrganizationInvitationStatus
{
    AccessDenied = 0,
    ExistingActiveMembership = 1,
    IncompatibleInactiveMembership = 2,
    DuplicatePendingInvitation = 3,
    Succeeded = 4
}
