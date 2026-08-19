using Enma.Application.Authorization;
using Enma.Application.Documents.Inspection;
using Enma.Application.Documents.Staging;
using Enma.Application.Documents.Storage;
using Enma.Application.Processes;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;

namespace Enma.Application.Documents.Upload;

public sealed class UploadLegalDocumentUseCase
{
    private readonly OrganizationAccessAuthorization _organizationAccessAuthorization;
    private readonly IActiveClientInOrganizationLookup _activeClientLookup;
    private readonly IProcessOrganizationOwnershipLookup _processOwnershipLookup;
    private readonly ILegalDocumentContentStager _contentStager;
    private readonly ILegalDocumentContentInspector _contentInspector;
    private readonly ILegalDocumentUploadPersistence _uploadPersistence;
    private readonly TimeProvider _timeProvider;

    public UploadLegalDocumentUseCase(
        OrganizationAccessAuthorization organizationAccessAuthorization,
        IActiveClientInOrganizationLookup activeClientLookup,
        IProcessOrganizationOwnershipLookup processOwnershipLookup,
        ILegalDocumentContentStager contentStager,
        ILegalDocumentContentInspector contentInspector,
        ILegalDocumentUploadPersistence uploadPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        ArgumentNullException.ThrowIfNull(activeClientLookup);
        ArgumentNullException.ThrowIfNull(processOwnershipLookup);
        ArgumentNullException.ThrowIfNull(contentStager);
        ArgumentNullException.ThrowIfNull(contentInspector);
        ArgumentNullException.ThrowIfNull(uploadPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _organizationAccessAuthorization = organizationAccessAuthorization;
        _activeClientLookup = activeClientLookup;
        _processOwnershipLookup = processOwnershipLookup;
        _contentStager = contentStager;
        _contentInspector = contentInspector;
        _uploadPersistence = uploadPersistence;
        _timeProvider = timeProvider;
    }

    public async Task<UploadLegalDocumentResult> ExecuteAsync(
        UploadLegalDocumentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        OrganizationAccessAuthorizationResult access;

        try
        {
            access = await _organizationAccessAuthorization.AuthorizeAsync(
                command.UserId,
                command.OrganizationId,
                cancellationToken);
        }
        catch (ArgumentOutOfRangeException exception) when (
            exception.ParamName == "role")
        {
            return UploadLegalDocumentResult.AccessDenied;
        }

        if (!TryGetAuthorizedUploadContext(
                command,
                access,
                out Guid actorMembershipId))
        {
            return UploadLegalDocumentResult.AccessDenied;
        }

        if (HasInvalidClassification(command))
        {
            return UploadLegalDocumentResult.InvalidInput;
        }

        try
        {
            LegalDocumentUploadAdmission admission =
                LegalDocumentUploadPolicy.Admit(
                    command.OriginalFileName,
                    command.SubmittedContentType,
                    command.DeclaredContentLength);

            await using ILegalDocumentStagedContent stagedContent =
                await _contentStager.StageAsync(
                    command.Content,
                    admission.ContentLength,
                    cancellationToken);

            await _contentInspector.InspectAsync(
                stagedContent.Content,
                stagedContent.ContentLength,
                admission.FileType,
                cancellationToken);

            UploadLegalDocumentResult? relationFailure =
                await ValidateClassificationAsync(
                    command,
                    cancellationToken);

            if (relationFailure is not null)
            {
                return relationFailure;
            }

            var contentHash = new LegalDocumentContentHash(
                stagedContent.ContentHashSha256.ToArray());

            LegalDocumentStorageObjectKey objectKey =
                LegalDocumentStorageObjectKey.CreateNew();

            var persistenceRequest =
                new LegalDocumentUploadPersistenceRequest(
                    command.UserId,
                    command.OrganizationId,
                    actorMembershipId,
                    command.ClientId,
                    command.ProcessId,
                    admission.OriginalFileName,
                    objectKey,
                    admission.CanonicalContentType,
                    stagedContent.ContentLength,
                    contentHash);

            LegalDocumentUploadPersistenceResult persistenceResult =
                await _uploadPersistence.ExecuteAsync(
                    persistenceRequest,
                    stagedContent.Content,
                    lockedState => DecideUpload(
                        persistenceRequest,
                        lockedState),
                    cancellationToken);

            return MapPersistenceResult(persistenceResult);
        }
        catch (LegalDocumentUploadRejectedException exception)
        {
            return UploadLegalDocumentResult.Rejected(exception.Reason);
        }
    }

    private static bool TryGetAuthorizedUploadContext(
        UploadLegalDocumentCommand command,
        OrganizationAccessAuthorizationResult access,
        out Guid actorMembershipId)
    {
        if (access.Status == OrganizationAccessAuthorizationStatus.Denied ||
            access.UserId is not Guid actorUserId ||
            access.OrganizationId is not Guid contextualOrganizationId ||
            access.MembershipId is not Guid contextualMembershipId ||
            access.Role is not OrganizationRole role ||
            actorUserId != command.UserId ||
            contextualOrganizationId != command.OrganizationId ||
            !IsUploadAllowed(role))
        {
            actorMembershipId = Guid.Empty;
            return false;
        }

        actorMembershipId = contextualMembershipId;
        return actorMembershipId != Guid.Empty;
    }

    private async Task<UploadLegalDocumentResult?> ValidateClassificationAsync(
        UploadLegalDocumentCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientId is Guid clientId)
        {
            bool clientExists = await _activeClientLookup.ExistsAsync(
                clientId,
                command.OrganizationId,
                cancellationToken);

            return clientExists
                ? null
                : UploadLegalDocumentResult.RelatedClientUnavailable;
        }

        if (command.ProcessId is Guid processId)
        {
            bool processExists =
                await _processOwnershipLookup.ExistsInOrganizationAsync(
                    processId,
                    command.OrganizationId,
                    cancellationToken);

            return processExists
                ? null
                : UploadLegalDocumentResult.RelatedProcessUnavailable;
        }

        return null;
    }

    private LegalDocumentUploadDecision DecideUpload(
        LegalDocumentUploadPersistenceRequest request,
        LegalDocumentUploadLockedState lockedState)
    {
        if (!IsAvailableActor(lockedState.Actor, request))
        {
            return LegalDocumentUploadDecision.AccessDenied;
        }

        if (request.ClientId is Guid clientId &&
            !IsAvailableClient(
                lockedState.Client,
                request.OrganizationId,
                clientId))
        {
            return LegalDocumentUploadDecision.RelatedClientUnavailable;
        }

        if (request.ProcessId is Guid processId &&
            !IsAvailableProcess(
                lockedState.Process,
                request.OrganizationId,
                processId))
        {
            return LegalDocumentUploadDecision.RelatedProcessUnavailable;
        }

        var legalDocument = new LegalDocument(
            request.OrganizationId,
            request.ClientId,
            request.ProcessId,
            request.OriginalFileName,
            request.ObjectKey.Value,
            request.CanonicalContentType,
            request.ContentLength,
            request.ContentHashSha256,
            request.ActorMembershipId,
            _timeProvider.GetUtcNow());

        return LegalDocumentUploadDecision.Persist(legalDocument);
    }

    private static UploadLegalDocumentResult MapPersistenceResult(
        LegalDocumentUploadPersistenceResult persistenceResult)
    {
        return persistenceResult.Status switch
        {
            LegalDocumentUploadPersistenceResultStatus.AccessDenied =>
                UploadLegalDocumentResult.AccessDenied,
            LegalDocumentUploadPersistenceResultStatus.RelatedClientUnavailable =>
                UploadLegalDocumentResult.RelatedClientUnavailable,
            LegalDocumentUploadPersistenceResultStatus.RelatedProcessUnavailable =>
                UploadLegalDocumentResult.RelatedProcessUnavailable,
            LegalDocumentUploadPersistenceResultStatus.Persisted
                when persistenceResult.DocumentId is Guid documentId =>
                UploadLegalDocumentResult.Succeeded(documentId),
            _ => throw new InvalidOperationException(
                "Legal document upload persistence returned an invalid result.")
        };
    }

    private static bool HasInvalidClassification(
        UploadLegalDocumentCommand command)
    {
        return command.ClientId == Guid.Empty ||
            command.ProcessId == Guid.Empty ||
            command.ClientId.HasValue && command.ProcessId.HasValue;
    }

    private static bool IsUploadAllowed(OrganizationRole role)
    {
        return role switch
        {
            OrganizationRole.Owner => true,
            OrganizationRole.Administrator => true,
            OrganizationRole.Member => true,
            _ => false
        };
    }

    private static bool IsAvailableActor(
        LegalDocumentUploadActorState? actor,
        LegalDocumentUploadPersistenceRequest request)
    {
        return actor is not null &&
            actor.UserId == request.UserId &&
            actor.OrganizationId == request.OrganizationId &&
            actor.MembershipId == request.ActorMembershipId &&
            actor.IsMembershipActive &&
            actor.IsUserActive &&
            actor.IsOrganizationActive &&
            IsUploadAllowed(actor.Role);
    }

    private static bool IsAvailableClient(
        LegalDocumentUploadClientState? client,
        Guid organizationId,
        Guid clientId)
    {
        return client is not null &&
            client.ClientId == clientId &&
            client.OrganizationId == organizationId &&
            client.IsActive;
    }

    private static bool IsAvailableProcess(
        LegalDocumentUploadProcessState? process,
        Guid organizationId,
        Guid processId)
    {
        return process is not null &&
            process.ProcessId == processId &&
            process.OrganizationId == organizationId;
    }
}
