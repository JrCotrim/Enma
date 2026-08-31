using Enma.Domain.Organizations;

namespace Enma.Application.Organizations.Invitations;

public interface IOrganizationInvitationMutationPersistence
{
    Task<PreviewOrganizationInvitationPersistenceResult> PreviewAsync(
        OrganizationInvitationTokenHash tokenHash,
        CancellationToken cancellationToken = default);

    Task<AcceptOrganizationInvitationPersistenceResult> AcceptAsync(
        Guid userId,
        OrganizationInvitationTokenHash tokenHash,
        CancellationToken cancellationToken = default);

    Task<CreateOrganizationInvitationPersistenceResult> CreateAsync(
        CreateOrganizationInvitationPersistenceRequest request,
        CancellationToken cancellationToken = default);

    Task<RevokeOrganizationInvitationPersistenceResult> RevokeAsync(
        OrganizationInvitationMutationPersistenceRequest request,
        CancellationToken cancellationToken = default);

    Task<ResendOrganizationInvitationPersistenceResult> ResendAsync(
        OrganizationInvitationMutationPersistenceRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record PreviewOrganizationInvitationPersistenceResult(
    PreviewOrganizationInvitationPersistenceStatus Status,
    string? OrganizationName = null,
    string? InvitedEmail = null,
    OrganizationRole? Role = null);

public enum PreviewOrganizationInvitationPersistenceStatus
{
    Invalid = 0,
    Expired = 1,
    Usable = 2
}

public enum AcceptOrganizationInvitationPersistenceResult
{
    Rejected = 0,
    Succeeded = 1
}

public sealed record CreateOrganizationInvitationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    string Email,
    OrganizationRole Role);

public sealed record OrganizationInvitationMutationPersistenceRequest(
    Guid UserId,
    Guid OrganizationId,
    Guid ActorMembershipId,
    Guid InvitationId);

public sealed record CreateOrganizationInvitationPersistenceResult(
    CreateOrganizationInvitationPersistenceStatus Status,
    Guid? InvitationId = null,
    OrganizationInvitationDeliveryRequest? DeliveryRequest = null);

public enum CreateOrganizationInvitationPersistenceStatus
{
    InvalidInput = 0,
    AccessDenied = 1,
    ExistingActiveMembership = 2,
    IncompatibleInactiveMembership = 3,
    DuplicatePendingInvitation = 4,
    Succeeded = 5
}

public enum RevokeOrganizationInvitationPersistenceResult
{
    InvalidInput = 0,
    AccessDenied = 1,
    NotFound = 2,
    Conflict = 3,
    Succeeded = 4
}

public sealed record ResendOrganizationInvitationPersistenceResult(
    ResendOrganizationInvitationPersistenceStatus Status,
    OrganizationInvitationDeliveryRequest? DeliveryRequest = null,
    TimeSpan? RetryAfter = null);

public enum ResendOrganizationInvitationPersistenceStatus
{
    InvalidInput = 0,
    AccessDenied = 1,
    NotFound = 2,
    Conflict = 3,
    Cooldown = 4,
    Succeeded = 5
}
