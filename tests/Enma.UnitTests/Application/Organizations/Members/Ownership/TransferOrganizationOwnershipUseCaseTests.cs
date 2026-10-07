using Enma.Application.Authorization;
using Enma.Application.Organizations.Members.Ownership;
using Enma.Application.Validation;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Organizations.Members.Ownership;

public sealed class TransferOrganizationOwnershipUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "2f0a4c55-7a34-4e1f-9b3a-0d6b0c1f3a01");
    private static readonly Guid OrganizationId = Guid.Parse(
        "7c1e9a2b-5d4f-4a6e-8b3c-2e1f0a9b8c02");
    private static readonly Guid ActorMembershipId = Guid.Parse(
        "a4b3c2d1-e0f9-4a8b-9c7d-6e5f4a3b2c03");
    private static readonly Guid TargetMembershipId = Guid.Parse(
        "c9d8e7f6-a5b4-4c3d-8e2f-1a0b9c8d7e04");

    [Theory]
    [InlineData("administrator", OrganizationRole.Administrator)]
    [InlineData("member", OrganizationRole.Member)]
    public async Task ExecuteAsync_OwnerWithSupportedExpectedRole_ForwardsLiveActor(
        string expectedTargetRole,
        OrganizationRole parsedExpectedTargetRole)
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence(
            OrganizationOwnershipTransferPersistenceResult.Succeeded);
        TransferOrganizationOwnershipUseCase useCase = Create(lookup, persistence);

        TransferOrganizationOwnershipResult result = await useCase.ExecuteAsync(
            CreateCommand(expectedTargetRole));

        Assert.Equal(TransferOrganizationOwnershipResult.Succeeded, result);
        OrganizationOwnershipTransferPersistenceRequest request = Assert.IsType<
            OrganizationOwnershipTransferPersistenceRequest>(persistence.Request);
        Assert.Equal(UserId, request.UserId);
        Assert.Equal(OrganizationId, request.OrganizationId);
        Assert.Equal(ActorMembershipId, request.ActorMembershipId);
        Assert.Equal(TargetMembershipId, request.TargetMembershipId);
        Assert.Equal(parsedExpectedTargetRole, request.ExpectedTargetRole);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Administrator")]
    [InlineData("ADMINISTRATOR")]
    [InlineData(" administrator")]
    [InlineData("owner")]
    [InlineData("Owner")]
    [InlineData("Member")]
    [InlineData("2")]
    [InlineData("unsupported")]
    public async Task ExecuteAsync_UnsupportedExpectedRole_RejectsBeforeAuthorization(
        string? expectedTargetRole)
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence(
            OrganizationOwnershipTransferPersistenceResult.Succeeded);
        TransferOrganizationOwnershipUseCase useCase = Create(lookup, persistence);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            useCase.ExecuteAsync(CreateCommand(expectedTargetRole)));

        Assert.Equal(0, lookup.CallCount);
        Assert.Equal(0, persistence.CallCount);
    }

    [Theory]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_NonOwner_DeniesWithoutPersistence(
        OrganizationRole actorRole)
    {
        var lookup = new MutableAccessLookup(actorRole);
        var persistence = new RecordingPersistence(
            OrganizationOwnershipTransferPersistenceResult.Succeeded);
        TransferOrganizationOwnershipUseCase useCase = Create(lookup, persistence);

        TransferOrganizationOwnershipResult result = await useCase.ExecuteAsync(
            CreateCommand("administrator"));

        Assert.Equal(TransferOrganizationOwnershipResult.AccessDenied, result);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutLiveAccess_DeniesWithoutPersistence()
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner)
        {
            HasAccess = false
        };
        var persistence = new RecordingPersistence(
            OrganizationOwnershipTransferPersistenceResult.Succeeded);
        TransferOrganizationOwnershipUseCase useCase = Create(lookup, persistence);

        TransferOrganizationOwnershipResult result = await useCase.ExecuteAsync(
            CreateCommand("administrator"));

        Assert.Equal(TransferOrganizationOwnershipResult.AccessDenied, result);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyTargetMembership_ReturnsNotFoundWithoutPersistence()
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence(
            OrganizationOwnershipTransferPersistenceResult.Succeeded);
        TransferOrganizationOwnershipUseCase useCase = Create(lookup, persistence);

        TransferOrganizationOwnershipResult result = await useCase.ExecuteAsync(
            CreateCommand("administrator") with { MembershipId = Guid.Empty });

        Assert.Equal(TransferOrganizationOwnershipResult.NotFound, result);
        Assert.Equal(0, persistence.CallCount);
    }

    [Theory]
    [InlineData(
        OrganizationOwnershipTransferPersistenceResult.AccessDenied,
        TransferOrganizationOwnershipResult.AccessDenied)]
    [InlineData(
        OrganizationOwnershipTransferPersistenceResult.NotFound,
        TransferOrganizationOwnershipResult.NotFound)]
    [InlineData(
        OrganizationOwnershipTransferPersistenceResult.TargetUnavailable,
        TransferOrganizationOwnershipResult.TargetUnavailable)]
    [InlineData(
        OrganizationOwnershipTransferPersistenceResult.Succeeded,
        TransferOrganizationOwnershipResult.Succeeded)]
    public async Task ExecuteAsync_MapsAuthoritativePersistenceResult(
        OrganizationOwnershipTransferPersistenceResult persistenceResult,
        TransferOrganizationOwnershipResult expected)
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence(persistenceResult);
        TransferOrganizationOwnershipUseCase useCase = Create(lookup, persistence);

        TransferOrganizationOwnershipResult result = await useCase.ExecuteAsync(
            CreateCommand("administrator"));

        Assert.Equal(expected, result);
        Assert.Equal(1, persistence.CallCount);
    }

    [Theory]
    [InlineData(OrganizationOwnershipTransferPersistenceResult.InvalidInput)]
    [InlineData((OrganizationOwnershipTransferPersistenceResult)999)]
    public async Task ExecuteAsync_UnexpectedPersistenceResult_FailsClosed(
        OrganizationOwnershipTransferPersistenceResult persistenceResult)
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence(persistenceResult);
        TransferOrganizationOwnershipUseCase useCase = Create(lookup, persistence);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useCase.ExecuteAsync(CreateCommand("administrator")));
    }

    [Fact]
    public async Task ExecuteAsync_FormerOwner_IsDeniedOnNextCall()
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence(
            OrganizationOwnershipTransferPersistenceResult.Succeeded);
        TransferOrganizationOwnershipUseCase useCase = Create(lookup, persistence);

        TransferOrganizationOwnershipResult first = await useCase.ExecuteAsync(
            CreateCommand("administrator"));
        lookup.Role = OrganizationRole.Administrator;
        TransferOrganizationOwnershipResult second = await useCase.ExecuteAsync(
            CreateCommand("administrator"));

        Assert.Equal(TransferOrganizationOwnershipResult.Succeeded, first);
        Assert.Equal(TransferOrganizationOwnershipResult.AccessDenied, second);
        Assert.Equal(1, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellationToAuthorizationAndPersistence()
    {
        using var cancellationSource = new CancellationTokenSource();
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence(
            OrganizationOwnershipTransferPersistenceResult.Succeeded);
        TransferOrganizationOwnershipUseCase useCase = Create(lookup, persistence);

        await useCase.ExecuteAsync(
            CreateCommand("administrator"),
            cancellationSource.Token);

        Assert.Equal(cancellationSource.Token, lookup.CancellationToken);
        Assert.Equal(cancellationSource.Token, persistence.CancellationToken);
    }

    private static TransferOrganizationOwnershipUseCase Create(
        MutableAccessLookup lookup,
        RecordingPersistence persistence)
    {
        return new TransferOrganizationOwnershipUseCase(
            new OrganizationAdministrationAuthorization(
                new OrganizationAccessAuthorization(lookup)),
            persistence);
    }

    private static TransferOrganizationOwnershipCommand CreateCommand(
        string? expectedTargetRole)
    {
        return new TransferOrganizationOwnershipCommand(
            UserId,
            OrganizationId,
            TargetMembershipId,
            expectedTargetRole);
    }

    private sealed class MutableAccessLookup(OrganizationRole role)
        : IOrganizationAccessLookup
    {
        public OrganizationRole Role { get; set; } = role;

        public bool HasAccess { get; init; } = true;

        public int CallCount { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<OrganizationRole?>(HasAccess ? Role : null);
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CancellationToken = cancellationToken;
            return Task.FromResult<OrganizationAccessLookupResult?>(HasAccess
                ? new(UserId, OrganizationId, ActorMembershipId, Role)
                : null);
        }
    }

    private sealed class RecordingPersistence(
        OrganizationOwnershipTransferPersistenceResult result)
        : IOrganizationOwnershipTransferPersistence
    {
        public int CallCount { get; private set; }

        public OrganizationOwnershipTransferPersistenceRequest? Request
        {
            get;
            private set;
        }

        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationOwnershipTransferPersistenceResult> ExecuteAsync(
            OrganizationOwnershipTransferPersistenceRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            CancellationToken = cancellationToken;
            return Task.FromResult(result);
        }
    }
}
