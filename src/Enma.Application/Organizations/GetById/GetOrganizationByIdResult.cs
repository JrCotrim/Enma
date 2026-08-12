namespace Enma.Application.Organizations.GetById;

public sealed class GetOrganizationByIdResult
{
    private GetOrganizationByIdResult(
        GetOrganizationByIdResultStatus status,
        OrganizationMetadataReadModel? organization)
    {
        Status = status;
        Organization = organization;
    }

    public GetOrganizationByIdResultStatus Status { get; }

    public OrganizationMetadataReadModel? Organization { get; }

    public static GetOrganizationByIdResult AccessDenied { get; } = new(
        GetOrganizationByIdResultStatus.AccessDenied,
        null);

    public static GetOrganizationByIdResult NotFound { get; } = new(
        GetOrganizationByIdResultStatus.NotFound,
        null);

    public static GetOrganizationByIdResult Success(
        OrganizationMetadataReadModel organization)
    {
        ArgumentNullException.ThrowIfNull(organization);

        return new GetOrganizationByIdResult(
            GetOrganizationByIdResultStatus.Succeeded,
            organization);
    }
}

public enum GetOrganizationByIdResultStatus
{
    AccessDenied = 0,
    NotFound = 1,
    Succeeded = 2
}

public sealed record OrganizationMetadataReadModel(
    Guid Id,
    string Name,
    string Slug,
    bool IsActive,
    DateTimeOffset CreatedAt);
