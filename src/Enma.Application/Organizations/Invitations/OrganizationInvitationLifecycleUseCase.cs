using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.Application.Organizations.Invitations;

public sealed class OrganizationInvitationLifecycleUseCase
{
    private readonly OrganizationAdministrationAuthorization authorization;
    private readonly IOrganizationInvitationReadQueries queries;
    private readonly IOrganizationInvitationMutationPersistence persistence;
    private readonly IOrganizationInvitationDelivery delivery;

    public OrganizationInvitationLifecycleUseCase(
        OrganizationAdministrationAuthorization authorization,
        IOrganizationInvitationReadQueries queries,
        IOrganizationInvitationMutationPersistence persistence,
        IOrganizationInvitationDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(delivery);

        this.authorization = authorization;
        this.queries = queries;
        this.persistence = persistence;
        this.delivery = delivery;
    }

    public Task<OrganizationInvitationLifecycleResult> RevokeAsync(
        Guid userId,
        Guid organizationId,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            userId,
            organizationId,
            invitationId,
            OrganizationAdministrationAction.RevokeInvitation,
            resend: false,
            cancellationToken);
    }

    public Task<OrganizationInvitationLifecycleResult> ResendAsync(
        Guid userId,
        Guid organizationId,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            userId,
            organizationId,
            invitationId,
            OrganizationAdministrationAction.ResendInvitation,
            resend: true,
            cancellationToken);
    }

    private async Task<OrganizationInvitationLifecycleResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        Guid invitationId,
        OrganizationAdministrationAction action,
        bool resend,
        CancellationToken cancellationToken)
    {
        OrganizationAdministrationAuthorizationResult authorizationResult =
            await authorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);

        if (!authorizationResult.Allows(
                OrganizationAdministrationAction.ListInvitations) ||
            authorizationResult.UserId != userId ||
            authorizationResult.OrganizationId != organizationId ||
            authorizationResult.MembershipId is not Guid actorMembershipId ||
            actorMembershipId == Guid.Empty)
        {
            return new OrganizationInvitationLifecycleResult(
                OrganizationInvitationLifecycleStatus.AccessDenied);
        }

        if (invitationId == Guid.Empty)
        {
            return new OrganizationInvitationLifecycleResult(
                OrganizationInvitationLifecycleStatus.NotFound);
        }

        OrganizationRole? targetRole = await queries.FindRoleAsync(
            organizationId,
            invitationId,
            cancellationToken);

        if (targetRole is null)
        {
            return new OrganizationInvitationLifecycleResult(
                OrganizationInvitationLifecycleStatus.NotFound);
        }

        if (!authorizationResult.Allows(action, targetRole.Value))
        {
            return new OrganizationInvitationLifecycleResult(
                OrganizationInvitationLifecycleStatus.AccessDenied);
        }

        var request = new OrganizationInvitationMutationPersistenceRequest(
            userId,
            organizationId,
            actorMembershipId,
            invitationId);

        if (!resend)
        {
            return MapRevokeResult(await persistence.RevokeAsync(
                request,
                cancellationToken));
        }

        ResendOrganizationInvitationPersistenceResult persistenceResult =
            await persistence.ResendAsync(request, cancellationToken);

        if (persistenceResult.Status !=
            ResendOrganizationInvitationPersistenceStatus.Succeeded)
        {
            return MapResendResult(persistenceResult);
        }

        OrganizationInvitationDeliveryRequest deliveryRequest =
            persistenceResult.DeliveryRequest
            ?? throw new InvalidOperationException(
                "Successful invitation resend must include delivery data.");
        OrganizationInvitationDeliveryResult deliveryResult =
            await delivery.DeliverAsync(deliveryRequest, cancellationToken);

        return new OrganizationInvitationLifecycleResult(
            OrganizationInvitationLifecycleStatus.Succeeded,
            deliveryResult);
    }

    private static OrganizationInvitationLifecycleResult MapRevokeResult(
        RevokeOrganizationInvitationPersistenceResult result)
    {
        return result switch
        {
            RevokeOrganizationInvitationPersistenceResult.AccessDenied =>
                new(OrganizationInvitationLifecycleStatus.AccessDenied),
            RevokeOrganizationInvitationPersistenceResult.NotFound =>
                new(OrganizationInvitationLifecycleStatus.NotFound),
            RevokeOrganizationInvitationPersistenceResult.Conflict =>
                new(OrganizationInvitationLifecycleStatus.Conflict),
            RevokeOrganizationInvitationPersistenceResult.Succeeded =>
                new(OrganizationInvitationLifecycleStatus.Succeeded),
            RevokeOrganizationInvitationPersistenceResult.InvalidInput =>
                throw new InvalidOperationException(
                    "Validated invitation revocation input was rejected by persistence."),
            _ => throw new InvalidOperationException(
                "Invitation revocation persistence returned an invalid result.")
        };
    }

    private static OrganizationInvitationLifecycleResult MapResendResult(
        ResendOrganizationInvitationPersistenceResult result)
    {
        return result.Status switch
        {
            ResendOrganizationInvitationPersistenceStatus.AccessDenied =>
                new(OrganizationInvitationLifecycleStatus.AccessDenied),
            ResendOrganizationInvitationPersistenceStatus.NotFound =>
                new(OrganizationInvitationLifecycleStatus.NotFound),
            ResendOrganizationInvitationPersistenceStatus.Conflict =>
                new(OrganizationInvitationLifecycleStatus.Conflict),
            ResendOrganizationInvitationPersistenceStatus.Cooldown =>
                new(
                    OrganizationInvitationLifecycleStatus.Cooldown,
                    RetryAfter: result.RetryAfter),
            ResendOrganizationInvitationPersistenceStatus.InvalidInput =>
                throw new InvalidOperationException(
                    "Validated invitation resend input was rejected by persistence."),
            _ => throw new InvalidOperationException(
                "Invitation resend persistence returned an invalid result.")
        };
    }
}

public sealed record OrganizationInvitationLifecycleResult(
    OrganizationInvitationLifecycleStatus Status,
    OrganizationInvitationDeliveryResult? DeliveryStatus = null,
    TimeSpan? RetryAfter = null);

public enum OrganizationInvitationLifecycleStatus
{
    AccessDenied = 0,
    NotFound = 1,
    Conflict = 2,
    Cooldown = 3,
    Succeeded = 4
}
