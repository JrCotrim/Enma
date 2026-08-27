using Enma.Application.Authorization;
using Enma.Application.Organizations.UpdateName;
using Enma.Application.Validation;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Organizations.UpdateName;

public sealed class UpdateOrganizationNameUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "dc48ba8c-734f-4469-8c2f-810c67c661b8");
    private static readonly Guid OrganizationId = Guid.Parse(
        "d994e50a-0fc5-4aba-8bec-d2701537497c");
    private static readonly Guid MembershipId = Guid.Parse(
        "9aa3a9d6-debb-4334-ad74-641c98fe2f65");

    [Fact]
    public async Task ExecuteAsync_Owner_PersistsAuthoritativeContext()
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence();
        UpdateOrganizationNameUseCase useCase = Create(lookup, persistence);

        UpdateOrganizationNameResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            "New Legal");

        Assert.Equal(UpdateOrganizationNameResult.Succeeded, result);
        OrganizationNameMutationPersistenceRequest request = Assert.IsType<
            OrganizationNameMutationPersistenceRequest>(persistence.Request);
        Assert.Equal(UserId, request.UserId);
        Assert.Equal(OrganizationId, request.OrganizationId);
        Assert.Equal(MembershipId, request.ActorMembershipId);
        Assert.Equal("New Legal", request.Name);
    }

    [Theory]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_NonOwner_DeniesWithoutPersistence(
        OrganizationRole role)
    {
        var lookup = new MutableAccessLookup(role);
        var persistence = new RecordingPersistence();
        UpdateOrganizationNameUseCase useCase = Create(lookup, persistence);

        UpdateOrganizationNameResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            "Denied Legal");

        Assert.Equal(UpdateOrganizationNameResult.AccessDenied, result);
        Assert.Equal(0, persistence.CallCount);
    }

    [Theory]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_OwnerRoleChanges_DeniesNextLiveRequest(
        OrganizationRole changedRole)
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence();
        UpdateOrganizationNameUseCase useCase = Create(lookup, persistence);

        UpdateOrganizationNameResult first = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            "First Legal");
        lookup.Role = changedRole;
        UpdateOrganizationNameResult second = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            "Second Legal");

        Assert.Equal(UpdateOrganizationNameResult.Succeeded, first);
        Assert.Equal(UpdateOrganizationNameResult.AccessDenied, second);
        Assert.Equal(1, persistence.CallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_InvalidName_MapsDomainValidationToRequestValidation(
        string? name)
    {
        var persistence = new DomainValidationPersistence();
        UpdateOrganizationNameUseCase useCase = Create(
            new MutableAccessLookup(OrganizationRole.Owner),
            persistence);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            useCase.ExecuteAsync(UserId, OrganizationId, name!));

        Assert.Equal(1, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_OverMaximumName_MapsDomainValidationToRequestValidation()
    {
        var persistence = new DomainValidationPersistence();
        UpdateOrganizationNameUseCase useCase = Create(
            new MutableAccessLookup(OrganizationRole.Owner),
            persistence);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            useCase.ExecuteAsync(
                UserId,
                OrganizationId,
                new string('x', 151)));
    }

    [Fact]
    public async Task ExecuteAsync_UsesDomainNameNormalization()
    {
        var persistence = new DomainValidationPersistence();
        UpdateOrganizationNameUseCase useCase = Create(
            new MutableAccessLookup(OrganizationRole.Owner),
            persistence);

        UpdateOrganizationNameResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            "  New Legal  ");

        Assert.Equal(UpdateOrganizationNameResult.Succeeded, result);
        Assert.Equal("New Legal", persistence.NormalizedName);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence();
        UpdateOrganizationNameUseCase useCase = Create(lookup, persistence);

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            "New Legal",
            cancellationSource.Token);

        Assert.Equal(cancellationSource.Token, lookup.CancellationToken);
        Assert.Equal(cancellationSource.Token, persistence.CancellationToken);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_WithoutAuthoritativeContext_DeniesWithoutPersistence(
        bool emptyUserId,
        bool emptyOrganizationId)
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence();
        UpdateOrganizationNameUseCase useCase = Create(lookup, persistence);

        UpdateOrganizationNameResult result = await useCase.ExecuteAsync(
            emptyUserId ? Guid.Empty : UserId,
            emptyOrganizationId ? Guid.Empty : OrganizationId,
            "New Legal");

        Assert.Equal(UpdateOrganizationNameResult.AccessDenied, result);
        Assert.Equal(0, persistence.CallCount);
    }

    private static UpdateOrganizationNameUseCase Create(
        MutableAccessLookup lookup,
        IOrganizationNameMutationPersistence persistence)
    {
        return new UpdateOrganizationNameUseCase(
            new OrganizationAdministrationAuthorization(
                new OrganizationAccessAuthorization(lookup)),
            persistence);
    }

    private sealed class MutableAccessLookup(OrganizationRole role)
        : IOrganizationAccessLookup
    {
        public OrganizationRole Role { get; set; } = role;

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
                userId,
                organizationId,
                MembershipId,
                Role));
        }
    }

    private sealed class RecordingPersistence : IOrganizationNameMutationPersistence
    {
        public int CallCount { get; private set; }

        public OrganizationNameMutationPersistenceRequest? Request { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationNameMutationPersistenceResult> ExecuteAsync(
            OrganizationNameMutationPersistenceRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            CancellationToken = cancellationToken;
            return Task.FromResult(
                OrganizationNameMutationPersistenceResult.Succeeded);
        }
    }

    private sealed class DomainValidationPersistence
        : IOrganizationNameMutationPersistence
    {
        public int CallCount { get; private set; }

        public string? NormalizedName { get; private set; }

        public Task<OrganizationNameMutationPersistenceResult> ExecuteAsync(
            OrganizationNameMutationPersistenceRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var organization = new Organization(
                "Current Legal",
                "current-legal",
                DateTimeOffset.UtcNow);
            organization.Rename(request.Name);
            NormalizedName = organization.Name;
            return Task.FromResult(
                OrganizationNameMutationPersistenceResult.Succeeded);
        }
    }
}
