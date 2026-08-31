using Enma.Application.Authorization;
using Enma.Application.Validation;

namespace Enma.Application.Organizations.Invitations;

public sealed class ListOrganizationInvitationsUseCase
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;

    private readonly OrganizationAdministrationAuthorization authorization;
    private readonly IOrganizationInvitationReadQueries queries;
    private readonly TimeProvider timeProvider;

    public ListOrganizationInvitationsUseCase(
        OrganizationAdministrationAuthorization authorization,
        IOrganizationInvitationReadQueries queries,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.authorization = authorization;
        this.queries = queries;
        this.timeProvider = timeProvider;
    }

    public async Task<ListOrganizationInvitationsResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        int pageNumber = 1,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        ValidatePagination(pageNumber, pageSize);
        OrganizationAdministrationAuthorizationResult authorizationResult =
            await authorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);

        if (!authorizationResult.Allows(
                OrganizationAdministrationAction.ListInvitations) ||
            authorizationResult.UserId != userId ||
            authorizationResult.OrganizationId != organizationId)
        {
            return ListOrganizationInvitationsResult.AccessDenied;
        }

        OrganizationInvitationPage page = await queries.ListAsync(
            new OrganizationInvitationQuery(
                organizationId,
                timeProvider.GetUtcNow().ToUniversalTime(),
                pageNumber,
                pageSize),
            cancellationToken);

        return ListOrganizationInvitationsResult.Success(
            page,
            pageNumber,
            pageSize);
    }

    private static void ValidatePagination(int pageNumber, int pageSize)
    {
        if (pageNumber < 1)
        {
            throw new RequestValidationException(
                "Page number must be at least 1.");
        }

        if (pageSize < 1 || pageSize > MaximumPageSize)
        {
            throw new RequestValidationException(
                $"Page size must be between 1 and {MaximumPageSize}.");
        }

        if (((long)pageNumber - 1) * pageSize > int.MaxValue)
        {
            throw new RequestValidationException(
                "The requested page offset is too large.");
        }
    }
}

public sealed record ListOrganizationInvitationsResult(
    ListOrganizationInvitationsStatus Status,
    IReadOnlyList<OrganizationInvitationReadModel> Items,
    int PageNumber,
    int PageSize,
    int TotalCount)
{
    public static ListOrganizationInvitationsResult AccessDenied { get; } =
        new(
            ListOrganizationInvitationsStatus.AccessDenied,
            Array.Empty<OrganizationInvitationReadModel>(),
            0,
            0,
            0);

    public static ListOrganizationInvitationsResult Success(
        OrganizationInvitationPage page,
        int pageNumber,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new ListOrganizationInvitationsResult(
            ListOrganizationInvitationsStatus.Succeeded,
            page.Items,
            pageNumber,
            pageSize,
            page.TotalCount);
    }
}

public enum ListOrganizationInvitationsStatus
{
    AccessDenied = 0,
    Succeeded = 1
}
