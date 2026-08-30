using Enma.Application.Authorization;

namespace Enma.Application.Tasks.Complete;

public sealed class CompleteLegalTaskUseCase
{
    private readonly OrganizationAccessAuthorization _organizationAccessAuthorization;
    private readonly LegalTaskMutationAuthorization _mutationAuthorization;
    private readonly ILegalTaskMutationPersistence _mutationPersistence;
    private readonly TimeProvider _timeProvider;

    public CompleteLegalTaskUseCase(
        OrganizationAccessAuthorization organizationAccessAuthorization,
        LegalTaskMutationAuthorization mutationAuthorization,
        ILegalTaskMutationPersistence mutationPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(organizationAccessAuthorization);
        ArgumentNullException.ThrowIfNull(mutationAuthorization);
        ArgumentNullException.ThrowIfNull(mutationPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _organizationAccessAuthorization = organizationAccessAuthorization;
        _mutationAuthorization = mutationAuthorization;
        _mutationPersistence = mutationPersistence;
        _timeProvider = timeProvider;
    }

    public async Task<CompleteLegalTaskResult> ExecuteAsync(
        CompleteLegalTaskCommand command,
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
            return CompleteLegalTaskResult.AccessDenied;
        }

        if (command.TaskId == Guid.Empty)
        {
            return CompleteLegalTaskResult.NotFound;
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
                state => Decide(request, state),
                cancellationToken);

        return persistenceResult switch
        {
            LegalTaskMutationPersistenceResult.AccessDenied =>
                CompleteLegalTaskResult.AccessDenied,
            LegalTaskMutationPersistenceResult.NotFound =>
                CompleteLegalTaskResult.NotFound,
            LegalTaskMutationPersistenceResult.Succeeded =>
                CompleteLegalTaskResult.Succeeded,
            _ => throw new InvalidOperationException(
                "Legal task completion returned an invalid result.")
        };
    }

    private LegalTaskMutationDecision Decide(
        LegalTaskMutationPersistenceRequest request,
        LegalTaskMutationLockedState state)
    {
        if (!state.IsOrganizationActive ||
            !LegalTaskMutationUseCaseSupport.IsAvailableActor(
                state.Actor,
                request))
        {
            return LegalTaskMutationDecision.AccessDenied;
        }

        if (!_mutationAuthorization.CanUpdateOrChangeLifecycle(
                state.Actor!.Role,
                state.Actor.MembershipId,
                LegalTaskMutationTaskState.From(state.LegalTask)))
        {
            return LegalTaskMutationDecision.AccessDenied;
        }

        if (state.LegalTask.CompletedAt is null)
        {
            state.LegalTask.Complete(_timeProvider.GetUtcNow());
        }

        return LegalTaskMutationDecision.Persist;
    }
}
