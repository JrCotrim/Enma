using Enma.Domain.Organizations;

namespace Enma.Application.Organizations.Invitations;

public interface IOrganizationInvitationReadQueries
{
    Task<OrganizationInvitationPage> ListAsync(
        OrganizationInvitationQuery query,
        CancellationToken cancellationToken = default);

    Task<OrganizationRole?> FindRoleAsync(
        Guid organizationId,
        Guid invitationId,
        CancellationToken cancellationToken = default);
}

public sealed record OrganizationInvitationQuery(
    Guid OrganizationId,
    DateTimeOffset Now,
    int PageNumber,
    int PageSize);

public sealed record OrganizationInvitationPage(
    IReadOnlyList<OrganizationInvitationReadModel> Items,
    int TotalCount);

public sealed record OrganizationInvitationReadModel(
    Guid Id,
    string InvitedEmail,
    OrganizationRole Role,
    OrganizationInvitationState Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    Guid CreatedByMembershipId);
