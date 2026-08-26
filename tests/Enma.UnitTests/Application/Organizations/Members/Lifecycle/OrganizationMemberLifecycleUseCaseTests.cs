using Enma.Application.Authorization;
using Enma.Application.Organizations.Members.Lifecycle;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Organizations.Members.Lifecycle;

public sealed class OrganizationMemberLifecycleUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "74932104-70a9-4750-b5fa-b36fb1495ceb");
    private static readonly Guid OrganizationId = Guid.Parse(
        "68930066-a117-4395-987e-d90a1d78a66d");
    private static readonly Guid ActorMembershipId = Guid.Parse(
        "90b3935b-a4df-492d-9be2-f809b02aa7e0");
    private static readonly Guid TargetMembershipId = Guid.Parse(
        "b946db16-4687-4995-a32f-adfcd07e37af");

    [Theory]
    [InlineData(OrganizationMemberLifecycleOperation.Deactivate,
        OrganizationRole.Owner)]
    [InlineData(OrganizationMemberLifecycleOperation.Deactivate,
        OrganizationRole.Administrator)]
    [InlineData(OrganizationMemberLifecycleOperation.Reactivate,
        OrganizationRole.Owner)]
    [InlineData(OrganizationMemberLifecycleOperation.Reactivate,
        OrganizationRole.Administrator)]
    public async Task ExecuteAsync_AuthorizedActor_PersistsRequestedOperation(
        OrganizationMemberLifecycleOperation operation,
        OrganizationRole actorRole)
    {
        var lookup = new MutableAccessLookup(actorRole);
        var persistence = new RecordingPersistence(
            OrganizationMemberLifecycleMutationPersistenceResult.Succeeded);
        OrganizationMemberLifecycleUseCase useCase = Create(lookup, persistence);

        OrganizationMemberLifecycleResult result = await ExecuteAsync(
            useCase,
            operation);

        Assert.Equal(OrganizationMemberLifecycleResult.Succeeded, result);
        OrganizationMemberLifecycleMutationPersistenceRequest request =
            Assert.IsType<OrganizationMemberLifecycleMutationPersistenceRequest>(
                persistence.Request);
        Assert.Equal(UserId, request.UserId);
        Assert.Equal(OrganizationId, request.OrganizationId);
        Assert.Equal(ActorMembershipId, request.ActorMembershipId);
        Assert.Equal(TargetMembershipId, request.TargetMembershipId);
        Assert.Equal(operation, request.Operation);
    }

    [Theory]
    [InlineData(OrganizationMemberLifecycleOperation.Deactivate)]
    [InlineData(OrganizationMemberLifecycleOperation.Reactivate)]
    public async Task ExecuteAsync_MemberActor_DeniesWithoutPersistence(
        OrganizationMemberLifecycleOperation operation)
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Member);
        var persistence = new RecordingPersistence(
            OrganizationMemberLifecycleMutationPersistenceResult.Succeeded);
        OrganizationMemberLifecycleUseCase useCase = Create(lookup, persistence);

        OrganizationMemberLifecycleResult result = await ExecuteAsync(
            useCase,
            operation);

        Assert.Equal(OrganizationMemberLifecycleResult.AccessDenied, result);
        Assert.Equal(0, persistence.CallCount);
    }

    [Theory]
    [InlineData(OrganizationMemberLifecycleOperation.Deactivate)]
    [InlineData(OrganizationMemberLifecycleOperation.Reactivate)]
    public async Task ExecuteAsync_EmptyTarget_ReturnsNotFoundWithoutPersistence(
        OrganizationMemberLifecycleOperation operation)
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence(
            OrganizationMemberLifecycleMutationPersistenceResult.Succeeded);
        OrganizationMemberLifecycleUseCase useCase = Create(lookup, persistence);

        OrganizationMemberLifecycleResult result = operation ==
            OrganizationMemberLifecycleOperation.Deactivate
                ? await useCase.DeactivateAsync(
                    UserId,
                    OrganizationId,
                    Guid.Empty)
                : await useCase.ReactivateAsync(
                    UserId,
                    OrganizationId,
                    Guid.Empty);

        Assert.Equal(OrganizationMemberLifecycleResult.NotFound, result);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_LiveRoleChangeIsAppliedOnNextRequest()
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Administrator);
        var persistence = new RecordingPersistence(
            OrganizationMemberLifecycleMutationPersistenceResult.Succeeded);
        OrganizationMemberLifecycleUseCase useCase = Create(lookup, persistence);

        OrganizationMemberLifecycleResult first = await useCase.DeactivateAsync(
            UserId,
            OrganizationId,
            TargetMembershipId);
        lookup.Role = OrganizationRole.Member;
        OrganizationMemberLifecycleResult second = await useCase.ReactivateAsync(
            UserId,
            OrganizationId,
            TargetMembershipId);

        Assert.Equal(OrganizationMemberLifecycleResult.Succeeded, first);
        Assert.Equal(OrganizationMemberLifecycleResult.AccessDenied, second);
        Assert.Equal(1, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_UntrustedAuthorizationIdentityFailsClosed()
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner)
        {
            ResultUserId = Guid.Parse("b84b82fe-596a-46c2-85aa-e1aa5c4f895c")
        };
        var persistence = new RecordingPersistence(
            OrganizationMemberLifecycleMutationPersistenceResult.Succeeded);
        OrganizationMemberLifecycleUseCase useCase = Create(lookup, persistence);

        OrganizationMemberLifecycleResult result = await useCase.DeactivateAsync(
            UserId,
            OrganizationId,
            TargetMembershipId);

        Assert.Equal(OrganizationMemberLifecycleResult.AccessDenied, result);
        Assert.Equal(0, persistence.CallCount);
    }

    [Theory]
    [InlineData(
        OrganizationMemberLifecycleMutationPersistenceResult.AccessDenied,
        OrganizationMemberLifecycleResult.AccessDenied)]
    [InlineData(
        OrganizationMemberLifecycleMutationPersistenceResult.NotFound,
        OrganizationMemberLifecycleResult.NotFound)]
    [InlineData(
        OrganizationMemberLifecycleMutationPersistenceResult.ActiveAssignmentsConflict,
        OrganizationMemberLifecycleResult.ActiveAssignmentsConflict)]
    [InlineData(
        OrganizationMemberLifecycleMutationPersistenceResult.InactiveUserConflict,
        OrganizationMemberLifecycleResult.InactiveUserConflict)]
    public async Task ExecuteAsync_MapsPersistenceResult(
        OrganizationMemberLifecycleMutationPersistenceResult persistenceResult,
        OrganizationMemberLifecycleResult expected)
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence(persistenceResult);

        OrganizationMemberLifecycleResult result = await Create(lookup, persistence)
            .DeactivateAsync(UserId, OrganizationId, TargetMembershipId);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence(
            OrganizationMemberLifecycleMutationPersistenceResult.Succeeded);

        await Create(lookup, persistence).DeactivateAsync(
            UserId,
            OrganizationId,
            TargetMembershipId,
            cancellationSource.Token);

        Assert.Equal(cancellationSource.Token, lookup.CancellationToken);
        Assert.Equal(cancellationSource.Token, persistence.CancellationToken);
    }

    private static OrganizationMemberLifecycleUseCase Create(
        MutableAccessLookup lookup,
        RecordingPersistence persistence)
    {
        return new OrganizationMemberLifecycleUseCase(
            new OrganizationAdministrationAuthorization(
                new OrganizationAccessAuthorization(lookup)),
            persistence);
    }

    private static Task<OrganizationMemberLifecycleResult> ExecuteAsync(
        OrganizationMemberLifecycleUseCase useCase,
        OrganizationMemberLifecycleOperation operation)
    {
        return operation == OrganizationMemberLifecycleOperation.Deactivate
            ? useCase.DeactivateAsync(
                UserId,
                OrganizationId,
                TargetMembershipId)
            : useCase.ReactivateAsync(
                UserId,
                OrganizationId,
                TargetMembershipId);
    }

    private sealed class MutableAccessLookup(OrganizationRole role)
        : IOrganizationAccessLookup
    {
        public OrganizationRole Role { get; set; } = role;

        public Guid ResultUserId { get; set; } = UserId;

        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<OrganizationRole?>(Role);
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CancellationToken = cancellationToken;
            return Task.FromResult<OrganizationAccessLookupResult?>(new(
                ResultUserId,
                OrganizationId,
                ActorMembershipId,
                Role));
        }
    }

    private sealed class RecordingPersistence(
        OrganizationMemberLifecycleMutationPersistenceResult result)
        : IOrganizationMemberLifecycleMutationPersistence
    {
        public int CallCount { get; private set; }

        public OrganizationMemberLifecycleMutationPersistenceRequest? Request
        {
            get;
            private set;
        }

        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationMemberLifecycleMutationPersistenceResult>
            ExecuteAsync(
                OrganizationMemberLifecycleMutationPersistenceRequest request,
                CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            CancellationToken = cancellationToken;
            return Task.FromResult(result);
        }
    }
}
