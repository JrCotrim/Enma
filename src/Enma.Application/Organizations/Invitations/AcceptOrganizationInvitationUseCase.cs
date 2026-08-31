namespace Enma.Application.Organizations.Invitations;

public sealed class AcceptOrganizationInvitationUseCase
{
    private readonly IOrganizationInvitationTokenService tokenService;
    private readonly IOrganizationInvitationMutationPersistence persistence;

    public AcceptOrganizationInvitationUseCase(
        IOrganizationInvitationTokenService tokenService,
        IOrganizationInvitationMutationPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(tokenService);
        ArgumentNullException.ThrowIfNull(persistence);

        this.tokenService = tokenService;
        this.persistence = persistence;
    }

    public async Task<AcceptOrganizationInvitationResult> ExecuteAsync(
        Guid userId,
        string? rawToken,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty ||
            !tokenService.TryHashToken(rawToken, out var tokenHash) ||
            tokenHash is null)
        {
            return AcceptOrganizationInvitationResult.Rejected;
        }

        AcceptOrganizationInvitationPersistenceResult result =
            await persistence.AcceptAsync(
                userId,
                tokenHash,
                cancellationToken);

        return result switch
        {
            AcceptOrganizationInvitationPersistenceResult.Rejected =>
                AcceptOrganizationInvitationResult.Rejected,
            AcceptOrganizationInvitationPersistenceResult.Succeeded =>
                AcceptOrganizationInvitationResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Invitation acceptance persistence returned an invalid result.")
        };
    }
}

public enum AcceptOrganizationInvitationResult
{
    Rejected = 0,
    Succeeded = 1
}
