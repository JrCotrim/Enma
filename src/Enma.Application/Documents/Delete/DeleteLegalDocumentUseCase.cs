using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.Application.Documents.Delete;

public sealed class DeleteLegalDocumentUseCase
{
    private readonly OrganizationAccessAuthorization accessAuthorization;
    private readonly ILegalDocumentDeletionPersistence deletionPersistence;
    private readonly TimeProvider timeProvider;

    public DeleteLegalDocumentUseCase(
        OrganizationAccessAuthorization accessAuthorization,
        ILegalDocumentDeletionPersistence deletionPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(accessAuthorization);
        ArgumentNullException.ThrowIfNull(deletionPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.accessAuthorization = accessAuthorization;
        this.deletionPersistence = deletionPersistence;
        this.timeProvider = timeProvider;
    }

    public async Task<DeleteLegalDocumentResult> ExecuteAsync(
        DeleteLegalDocumentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.UserId == Guid.Empty ||
            command.OrganizationId == Guid.Empty ||
            command.DocumentId == Guid.Empty)
        {
            return DeleteLegalDocumentResult.InvalidInput;
        }

        OrganizationAccessAuthorizationResult access;

        try
        {
            access = await accessAuthorization.AuthorizeAsync(
                command.UserId,
                command.OrganizationId,
                cancellationToken);
        }
        catch (ArgumentOutOfRangeException exception) when (
            exception.ParamName == "role")
        {
            return DeleteLegalDocumentResult.AccessDenied;
        }

        if (!TryGetAuthorizedContext(command, access, out Guid membershipId))
        {
            return DeleteLegalDocumentResult.AccessDenied;
        }

        var request = new LegalDocumentDeletionPersistenceRequest(
            command.UserId,
            command.OrganizationId,
            membershipId,
            command.DocumentId,
            timeProvider.GetUtcNow());
        LegalDocumentDeletionPersistenceResult persistenceResult =
            await deletionPersistence.RequestAsync(
                request,
                state => Decide(request, state),
                cancellationToken);

        return persistenceResult switch
        {
            LegalDocumentDeletionPersistenceResult.AccessDenied =>
                DeleteLegalDocumentResult.AccessDenied,
            LegalDocumentDeletionPersistenceResult.NotFound =>
                DeleteLegalDocumentResult.NotFound,
            LegalDocumentDeletionPersistenceResult.Accepted =>
                DeleteLegalDocumentResult.Accepted,
            _ => throw new InvalidOperationException(
                "Legal document deletion persistence returned an invalid result.")
        };
    }

    private static bool TryGetAuthorizedContext(
        DeleteLegalDocumentCommand command,
        OrganizationAccessAuthorizationResult access,
        out Guid membershipId)
    {
        bool allowed =
            access.Status == OrganizationAccessAuthorizationStatus.Allowed &&
            access.UserId == command.UserId &&
            access.OrganizationId == command.OrganizationId &&
            access.MembershipId is Guid contextualMembershipId &&
            contextualMembershipId != Guid.Empty &&
            access.Role is OrganizationRole.Owner or
                OrganizationRole.Administrator;

        membershipId = allowed
            ? access.MembershipId!.Value
            : Guid.Empty;
        return allowed;
    }

    private static LegalDocumentDeletionDecision Decide(
        LegalDocumentDeletionPersistenceRequest request,
        LegalDocumentDeletionLockedState state)
    {
        LegalDocumentDeletionActorState? actor = state.Actor;

        if (actor is null ||
            actor.UserId != request.UserId ||
            actor.OrganizationId != request.OrganizationId ||
            actor.MembershipId != request.ActorMembershipId ||
            !actor.IsMembershipActive ||
            !actor.IsUserActive ||
            !actor.IsOrganizationActive ||
            actor.Role is not (
                OrganizationRole.Owner or OrganizationRole.Administrator))
        {
            return LegalDocumentDeletionDecision.AccessDenied;
        }

        return state.Document is
            {
                DocumentId: var documentId,
                OrganizationId: var organizationId
            } &&
            documentId == request.DocumentId &&
            organizationId == request.OrganizationId
                ? LegalDocumentDeletionDecision.Accept
                : LegalDocumentDeletionDecision.NotFound;
    }
}
