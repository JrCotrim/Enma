namespace Enma.Application.Organizations.CurrentUser;

public interface ICurrentUserOrganizationQueries
{
    Task<IReadOnlyList<CurrentUserOrganizationReadModel>> ListAccessibleAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
