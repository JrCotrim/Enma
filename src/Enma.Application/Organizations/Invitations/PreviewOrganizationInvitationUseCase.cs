using Enma.Domain.Organizations;

namespace Enma.Application.Organizations.Invitations;

public sealed class PreviewOrganizationInvitationUseCase
{
    private readonly IOrganizationInvitationTokenService tokenService;
    private readonly IOrganizationInvitationMutationPersistence persistence;

    public PreviewOrganizationInvitationUseCase(
        IOrganizationInvitationTokenService tokenService,
        IOrganizationInvitationMutationPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(tokenService);
        ArgumentNullException.ThrowIfNull(persistence);

        this.tokenService = tokenService;
        this.persistence = persistence;
    }

    public async Task<PreviewOrganizationInvitationResult> ExecuteAsync(
        string? rawToken,
        CancellationToken cancellationToken = default)
    {
        if (!tokenService.TryHashToken(rawToken, out var tokenHash) ||
            tokenHash is null)
        {
            return PreviewOrganizationInvitationResult.Invalid;
        }

        PreviewOrganizationInvitationPersistenceResult result =
            await persistence.PreviewAsync(tokenHash, cancellationToken);

        return result.Status switch
        {
            PreviewOrganizationInvitationPersistenceStatus.Invalid =>
                PreviewOrganizationInvitationResult.Invalid,
            PreviewOrganizationInvitationPersistenceStatus.Expired =>
                PreviewOrganizationInvitationResult.Expired,
            PreviewOrganizationInvitationPersistenceStatus.Usable when
                result.OrganizationName is not null &&
                result.InvitedEmail is not null &&
                result.Role is OrganizationRole.Administrator or
                    OrganizationRole.Member =>
                new PreviewOrganizationInvitationResult(
                    PreviewOrganizationInvitationStatus.Usable,
                    result.OrganizationName,
                    result.Role,
                    MaskEmail(result.InvitedEmail)),
            _ => throw new InvalidOperationException(
                "Invitation preview persistence returned an invalid result.")
        };
    }

    private static string MaskEmail(string email)
    {
        int atSignIndex = email.IndexOf('@');

        if (atSignIndex <= 0 || atSignIndex == email.Length - 1)
        {
            throw new InvalidOperationException(
                "Invitation preview persistence returned an invalid email.");
        }

        return $"{email[0]}***{email[atSignIndex..]}";
    }
}

public sealed record PreviewOrganizationInvitationResult(
    PreviewOrganizationInvitationStatus Status,
    string? OrganizationName = null,
    OrganizationRole? Role = null,
    string? InvitedEmail = null)
{
    public static PreviewOrganizationInvitationResult Invalid { get; } =
        new(PreviewOrganizationInvitationStatus.Invalid);

    public static PreviewOrganizationInvitationResult Expired { get; } =
        new(PreviewOrganizationInvitationStatus.Expired);
}

public enum PreviewOrganizationInvitationStatus
{
    Invalid = 0,
    Expired = 1,
    Usable = 2
}
