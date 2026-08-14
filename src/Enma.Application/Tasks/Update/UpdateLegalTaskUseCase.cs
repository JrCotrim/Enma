using Enma.Application.Authorization;
using Enma.Domain.Tasks;

namespace Enma.Application.Tasks.Update;

public sealed class UpdateLegalTaskUseCase
{
    private readonly OrganizationAccessAuthorization _organizationAccessAuthorization;
    private readonly LegalTaskMutationAuthorization _mutationAuthorization;
    private readonly ILegalTaskMutationPersistence _mutationPersistence;

    public UpdateLegalTaskUseCase(
        OrganizationAccessAuthorization organizationAccessAuthorization,
        LegalTaskMutationAuthorization mutationAuthorization,
        ILegalTaskMutationPersistence mutationPersistence)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        ArgumentNullException.ThrowIfNull(mutationAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);

        _organizationAccessAuthorization = organizationAccessAuthorization;
        _mutationAuthorization = mutationAuthorization;
        _mutationPersistence = mutationPersistence;
    }

    public async Task<UpdateLegalTaskResult> ExecuteAsync(
        UpdateLegalTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        LegalTaskMutationAccess? access =
            await LegalTaskMutationUseCaseSupport.GetAccessAsync(
                _organizationAccessAuthorization,
                command.UserId,
                command.OrganizationId,
                cancellationToken);

        if (access is null)
        {
            return UpdateLegalTaskResult.AccessDenied;
        }

        if (command.TaskId == Guid.Empty)
        {
            return UpdateLegalTaskResult.NotFound;
        }

        var request = new LegalTaskMutationPersistenceRequest(
            access.UserId,
            access.OrganizationId,
            access.MembershipId,
            command.TaskId);

        LegalTaskMutationPersistenceResult persistenceResult =
            await _mutationPersistence.ExecuteAsync(
                request,
                static _ => null,
                state => Decide(command, request, state),
                cancellationToken);

        return persistenceResult switch
        {
            LegalTaskMutationPersistenceResult.AccessDenied =>
                UpdateLegalTaskResult.AccessDenied,
            LegalTaskMutationPersistenceResult.NotFound =>
                UpdateLegalTaskResult.NotFound,
            LegalTaskMutationPersistenceResult.RelatedProcessUnavailable =>
                UpdateLegalTaskResult.RelatedProcessUnavailable,
            LegalTaskMutationPersistenceResult.InvalidInput =>
                UpdateLegalTaskResult.InvalidInput,
            LegalTaskMutationPersistenceResult.Conflict =>
                UpdateLegalTaskResult.Conflict,
            LegalTaskMutationPersistenceResult.Succeeded =>
                UpdateLegalTaskResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Legal task mutation persistence returned an invalid result.")
        };
    }

    private LegalTaskMutationDecision Decide(
        UpdateLegalTaskCommand command,
        LegalTaskMutationPersistenceRequest request,
        LegalTaskMutationLockedState state)
    {
        if (!LegalTaskMutationUseCaseSupport.IsAvailableActor(
                state.Actor,
                request))
        {
            return LegalTaskMutationDecision.AccessDenied;
        }

        LegalTaskMutationTaskState taskState =
            LegalTaskMutationTaskState.From(state.LegalTask);

        if (!_mutationAuthorization.CanUpdateOrChangeLifecycle(
                state.Actor!.Role,
                state.Actor.MembershipId,
                taskState))
        {
            return LegalTaskMutationDecision.AccessDenied;
        }

        if (state.LegalTask.CompletedAt is not null)
        {
            return LegalTaskMutationDecision.Conflict;
        }

        if (command.ProcessId == Guid.Empty ||
            command.DueDate == DateOnly.MinValue)
        {
            return LegalTaskMutationDecision.InvalidInput;
        }

        if (command.ProcessId is Guid processId &&
            processId != state.LegalTask.ProcessId)
        {
            if (state.ValidatedProcessId != processId ||
                state.IsProcessAvailable is null)
            {
                return LegalTaskMutationDecision.ValidateProcess(processId);
            }

            if (state.IsProcessAvailable == false)
            {
                return LegalTaskMutationDecision.RelatedProcessUnavailable;
            }
        }

        try
        {
            state.LegalTask.ChangeDetails(
                command.Title,
                command.Description,
                command.DueDate,
                command.ProcessId);

            return LegalTaskMutationDecision.Persist;
        }
        catch (LegalTaskCompletedMutationException)
        {
            return LegalTaskMutationDecision.Conflict;
        }
        catch (ArgumentException exception) when (
            exception.ParamName is "title" or
                "description" or
                "dueDate" or
                "processId")
        {
            return LegalTaskMutationDecision.InvalidInput;
        }
    }
}
