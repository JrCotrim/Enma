using Enma.Application.Validation;

namespace Enma.Application.Organizations.CurrentUser;

public sealed class GetCurrentUserOrganizationsUseCase
{
    private readonly ICurrentUserOrganizationQueries _queries;

    public GetCurrentUserOrganizationsUseCase(
        ICurrentUserOrganizationQueries queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        _queries = queries;
    }

    public Task<IReadOnlyList<CurrentUserOrganizationReadModel>> ExecuteAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new RequestValidationException("User id cannot be empty.");
        }

        return _queries.ListAccessibleAsync(userId, cancellationToken);
    }
}
