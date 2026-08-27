using Enma.Application.Authorization;
using Enma.Application.Validation;

namespace Enma.Application.Organizations.UpdateName;

public sealed class UpdateOrganizationNameUseCase
{
    private readonly OrganizationAdministrationAuthorization _authorization;
    private readonly IOrganizationNameMutationPersistence _persistence;

    public UpdateOrganizationNameUseCase(
        OrganizationAdministrationAuthorization authorization,
        IOrganizationNameMutationPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(persistence);

        _authorization = authorization;
        _persistence = persistence;
    }

    public async Task<UpdateOrganizationNameResult> ExecuteAsync(
        Guid userId,
        Guid organizationId,
        string name,
        CancellationToken cancellationToken = default)
    {
        OrganizationAdministrationAuthorizationResult authorization =
            await _authorization.AuthorizeAsync(
                userId,
                organizationId,
                cancellationToken);

        if (!authorization.Allows(
                OrganizationAdministrationAction.EditOrganization) ||
            authorization.UserId != userId ||
            authorization.OrganizationId != organizationId ||
            authorization.MembershipId is not Guid actorMembershipId ||
            actorMembershipId == Guid.Empty)
        {
            return UpdateOrganizationNameResult.AccessDenied;
        }

        OrganizationNameMutationPersistenceResult persistenceResult;

        try
        {
            persistenceResult = await _persistence.ExecuteAsync(
                new OrganizationNameMutationPersistenceRequest(
                    userId,
                    organizationId,
                    actorMembershipId,
                    name),
                cancellationToken);
        }
        catch (ArgumentException exception) when (exception.ParamName == "name")
        {
            throw new RequestValidationException(exception.Message, exception);
        }

        return persistenceResult switch
        {
            OrganizationNameMutationPersistenceResult.AccessDenied =>
                UpdateOrganizationNameResult.AccessDenied,
            OrganizationNameMutationPersistenceResult.Succeeded =>
                UpdateOrganizationNameResult.Succeeded,
            OrganizationNameMutationPersistenceResult.InvalidInput =>
                throw new InvalidOperationException(
                    "Validated organization name mutation input was rejected by persistence."),
            _ => throw new InvalidOperationException(
                "Organization name mutation persistence returned an invalid result.")
        };
    }
}

public enum UpdateOrganizationNameResult
{
    AccessDenied = 0,
    Succeeded = 1
}
