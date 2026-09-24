using Enma.Application.Authorization;
using Enma.Application.Documents.Delete;
using Enma.Application.Documents.Storage;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Documents.Delete;

public sealed class DeleteLegalDocumentUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "7bdf07d7-c119-4c3c-bf7c-204609f5ce93");
    private static readonly Guid OrganizationId = Guid.Parse(
        "2fecc709-bfea-4efc-86b8-ef6eb6cb1ee7");
    private static readonly Guid MembershipId = Guid.Parse(
        "40b7e5ab-c371-4a64-ac77-f65418ce34aa");
    private static readonly Guid DocumentId = Guid.Parse(
        "1409135a-b294-4738-a2a2-1d8624d7fec0");
    private static readonly DateTimeOffset RequestedAt = new(
        2026, 9, 23, 16, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_WithPrivilegedLiveActor_AcceptsDeletion(
        OrganizationRole role)
    {
        var persistence = new StubDeletionPersistence
        {
            LockedState = CreateLockedState(role)
        };
        DeleteLegalDocumentUseCase useCase = CreateUseCase(role, persistence);

        DeleteLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand());

        Assert.Equal(DeleteLegalDocumentResultStatus.Accepted, result.Status);
        Assert.NotNull(persistence.Request);
        Assert.Equal(MembershipId, persistence.Request.ActorMembershipId);
        Assert.Equal(RequestedAt, persistence.Request.RequestedAt);
    }

    [Fact]
    public async Task ExecuteAsync_WithMemberRole_DeniesBeforePersistence()
    {
        var persistence = new StubDeletionPersistence();
        DeleteLegalDocumentUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            persistence);

        DeleteLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand());

        Assert.Equal(DeleteLegalDocumentResultStatus.AccessDenied, result.Status);
        Assert.Null(persistence.Request);
    }

    [Fact]
    public async Task ExecuteAsync_WhenLockedActorLostPrivilege_DeniesDeletion()
    {
        var persistence = new StubDeletionPersistence
        {
            LockedState = CreateLockedState(OrganizationRole.Member)
        };
        DeleteLegalDocumentUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence);

        DeleteLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand());

        Assert.Equal(DeleteLegalDocumentResultStatus.AccessDenied, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDocumentIsOutsideTenant_ReturnsNotFound()
    {
        var persistence = new StubDeletionPersistence
        {
            LockedState = new LegalDocumentDeletionLockedState(
                CreateActor(OrganizationRole.Owner),
                new LegalDocumentDeletionDocumentState(
                    DocumentId,
                    Guid.NewGuid(),
                    false))
        };
        DeleteLegalDocumentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        DeleteLegalDocumentResult result = await useCase.ExecuteAsync(
            CreateCommand());

        Assert.Equal(DeleteLegalDocumentResultStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyIdentifier_ReturnsInvalidInput()
    {
        var persistence = new StubDeletionPersistence();
        DeleteLegalDocumentUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        DeleteLegalDocumentResult result = await useCase.ExecuteAsync(
            new DeleteLegalDocumentCommand(
                UserId,
                OrganizationId,
                Guid.Empty));

        Assert.Equal(DeleteLegalDocumentResultStatus.InvalidInput, result.Status);
        Assert.Null(persistence.Request);
    }

    private static DeleteLegalDocumentUseCase CreateUseCase(
        OrganizationRole role,
        StubDeletionPersistence persistence)
    {
        var access = new OrganizationAccessLookupResult(
            UserId,
            OrganizationId,
            MembershipId,
            role);

        return new DeleteLegalDocumentUseCase(
            new OrganizationAccessAuthorization(new StubAccessLookup(access)),
            persistence,
            new FixedTimeProvider(RequestedAt));
    }

    private static DeleteLegalDocumentCommand CreateCommand() => new(
        UserId,
        OrganizationId,
        DocumentId);

    private static LegalDocumentDeletionLockedState CreateLockedState(
        OrganizationRole role) => new(
            CreateActor(role),
            new LegalDocumentDeletionDocumentState(
                DocumentId,
                OrganizationId,
                false));

    private static LegalDocumentDeletionActorState CreateActor(
        OrganizationRole role) => new(
            UserId,
            OrganizationId,
            MembershipId,
            role,
            true,
            true,
            true);

    private sealed class StubAccessLookup(
        OrganizationAccessLookupResult access) : IOrganizationAccessLookup
    {
        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<OrganizationRole?>(access.Role);

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<OrganizationAccessLookupResult?>(access);
    }

    private sealed class StubDeletionPersistence
        : ILegalDocumentDeletionPersistence
    {
        public LegalDocumentDeletionLockedState LockedState { get; set; } =
            CreateLockedState(OrganizationRole.Owner);

        public LegalDocumentDeletionPersistenceRequest? Request { get; private set; }

        public Task<LegalDocumentDeletionPersistenceResult> RequestAsync(
            LegalDocumentDeletionPersistenceRequest request,
            Func<LegalDocumentDeletionLockedState, LegalDocumentDeletionDecision> decide,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            LegalDocumentDeletionPersistenceResult result = decide(LockedState) switch
            {
                LegalDocumentDeletionDecision.AccessDenied =>
                    LegalDocumentDeletionPersistenceResult.AccessDenied,
                LegalDocumentDeletionDecision.NotFound =>
                    LegalDocumentDeletionPersistenceResult.NotFound,
                LegalDocumentDeletionDecision.Accept =>
                    LegalDocumentDeletionPersistenceResult.Accepted,
                _ => throw new InvalidOperationException()
            };
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<PendingLegalDocumentDeletion>> ListPendingAsync(
            int maximumCount,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PendingLegalDocumentDeletion>>([]);

        public Task<bool> FinalizeAsync(
            PendingLegalDocumentDeletion deletion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
