namespace Enma.Application.Onboarding;

public interface IInitialOrganizationPersistence
{
    Task<InitialOrganizationResult> CreateAsync(
        Guid userId,
        string provider,
        string providerSubject,
        string organizationName,
        string organizationSlug,
        CancellationToken cancellationToken = default);
}

public enum InitialOrganizationStatus
{
    Succeeded,
    Ineligible,
    SlugConflict,
    Invalid
}

public sealed record InitialOrganizationResult(
    InitialOrganizationStatus Status,
    Guid? OrganizationId = null);

public sealed class CreateInitialOrganizationUseCase(
    IInitialOrganizationPersistence persistence)
{
    public Task<InitialOrganizationResult> ExecuteAsync(
        Guid userId,
        string provider,
        string providerSubject,
        string organizationName,
        string organizationSlug,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty ||
            string.IsNullOrWhiteSpace(provider) ||
            string.IsNullOrWhiteSpace(providerSubject))
        {
            return Task.FromResult(new InitialOrganizationResult(
                InitialOrganizationStatus.Ineligible));
        }

        return persistence.CreateAsync(
            userId,
            provider,
            providerSubject,
            organizationName,
            organizationSlug,
            cancellationToken);
    }
}
