using Enma.Application.Organizations.Invitations;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;

namespace Enma.Application.Authentication;

public interface IExternalAuthenticationPersistence
{
    Task<ExternalAuthenticationPersistenceResult> CompleteAsync(
        string provider,
        string providerSubject,
        string normalizedEmail,
        string? name,
        OrganizationInvitationTokenHash? invitationTokenHash,
        CancellationToken cancellationToken = default);

    Task<bool> LinkAsync(
        Guid userId,
        string provider,
        string providerSubject,
        string normalizedEmail,
        CancellationToken cancellationToken = default);
}

public enum ExternalAuthenticationPersistenceStatus
{
    Succeeded,
    LinkRequired,
    ProfileRequired,
    InvalidInvitation,
    WrongInvitationRecipient,
    Rejected
}

public sealed record ExternalAuthenticationPersistenceResult(
    ExternalAuthenticationPersistenceStatus Status,
    Guid? UserId = null,
    long? CredentialVersion = null);

public enum ExternalAuthenticationStatus
{
    Succeeded,
    LinkRequired,
    ProfileRequired,
    InvalidProviderIdentity,
    InvalidInvitation,
    WrongInvitationRecipient,
    Rejected
}

public sealed record ExternalAuthenticationResult(
    ExternalAuthenticationStatus Status,
    string? SessionHandle = null);

public sealed class ExternalAuthenticationUseCase
{
    private readonly IExternalAuthenticationPersistence persistence;
    private readonly IOrganizationInvitationTokenService invitationTokenService;
    private readonly IAuthenticationSessionHandleService sessionHandleService;
    private readonly IAuthenticationSessionIssuancePersistence sessionPersistence;
    private readonly TimeProvider timeProvider;

    public ExternalAuthenticationUseCase(
        IExternalAuthenticationPersistence persistence,
        IOrganizationInvitationTokenService invitationTokenService,
        IAuthenticationSessionHandleService sessionHandleService,
        IAuthenticationSessionIssuancePersistence sessionPersistence,
        TimeProvider timeProvider)
    {
        this.persistence = persistence;
        this.invitationTokenService = invitationTokenService;
        this.sessionHandleService = sessionHandleService;
        this.sessionPersistence = sessionPersistence;
        this.timeProvider = timeProvider;
    }

    public async Task<ExternalAuthenticationResult> CompleteAsync(
        string provider,
        string? providerSubject,
        string? email,
        bool emailVerified,
        string? name,
        string? invitationToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider) ||
            string.IsNullOrWhiteSpace(providerSubject) ||
            !emailVerified)
        {
            return new(ExternalAuthenticationStatus.InvalidProviderIdentity);
        }

        string normalizedEmail;
        try
        {
            normalizedEmail = User.NormalizeEmail(email ?? string.Empty);
        }
        catch (ArgumentException)
        {
            return new(ExternalAuthenticationStatus.InvalidProviderIdentity);
        }

        OrganizationInvitationTokenHash? invitationTokenHash = null;
        if (invitationToken is not null &&
            (!invitationTokenService.TryHashToken(
                    invitationToken,
                    out invitationTokenHash) ||
                invitationTokenHash is null))
        {
            return new(ExternalAuthenticationStatus.InvalidInvitation);
        }

        ExternalAuthenticationPersistenceResult persisted =
            await persistence.CompleteAsync(
                provider,
                providerSubject,
                normalizedEmail,
                name,
                invitationTokenHash,
                cancellationToken);

        if (persisted.Status != ExternalAuthenticationPersistenceStatus.Succeeded)
        {
            return new(persisted.Status switch
            {
                ExternalAuthenticationPersistenceStatus.LinkRequired =>
                    ExternalAuthenticationStatus.LinkRequired,
                ExternalAuthenticationPersistenceStatus.ProfileRequired =>
                    ExternalAuthenticationStatus.ProfileRequired,
                ExternalAuthenticationPersistenceStatus.InvalidInvitation =>
                    ExternalAuthenticationStatus.InvalidInvitation,
                ExternalAuthenticationPersistenceStatus.WrongInvitationRecipient =>
                    ExternalAuthenticationStatus.WrongInvitationRecipient,
                _ => ExternalAuthenticationStatus.Rejected
            });
        }

        Guid userId = persisted.UserId ?? throw new InvalidOperationException(
            "Successful external authentication requires a user id.");
        long credentialVersion = persisted.CredentialVersion ??
            throw new InvalidOperationException(
                "Successful external authentication requires a credential version.");
        string rawHandle = sessionHandleService.GenerateHandle(out var secretHash);
        DateTimeOffset createdAt = timeProvider.GetUtcNow();
        var session = new AuthenticationSession(
            userId,
            secretHash,
            credentialVersion,
            createdAt,
            createdAt.Add(AuthenticationSessionPolicy.IdleLifetime),
            createdAt.Add(AuthenticationSessionPolicy.AbsoluteLifetime));

        AuthenticationSessionIssuancePersistenceResult sessionResult =
            await sessionPersistence.TryPersistAsync(
                session,
                upgradedPasswordHash: null,
                cancellationToken);

        return sessionResult == AuthenticationSessionIssuancePersistenceResult.Succeeded
            ? new(ExternalAuthenticationStatus.Succeeded, rawHandle)
            : new(ExternalAuthenticationStatus.Rejected);
    }

    public Task<bool> LinkAsync(
        Guid userId,
        string provider,
        string providerSubject,
        string email,
        CancellationToken cancellationToken = default)
    {
        string normalizedEmail;
        try
        {
            normalizedEmail = User.NormalizeEmail(email);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(false);
        }

        return persistence.LinkAsync(
            userId,
            provider,
            providerSubject,
            normalizedEmail,
            cancellationToken);
    }
}
