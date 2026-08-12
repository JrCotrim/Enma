using Enma.Application.Authorization;
using Enma.Application.Organizations;
using Enma.Application.Organizations.GetById;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Organizations.GetById;

public sealed class GetOrganizationByIdHandlerTests
{
    private static readonly Guid UserId = Guid.Parse(
        "64ca0f8c-babe-4278-8222-75cb71638804");

    private static readonly Guid OrganizationId = Guid.Parse(
        "f1ca5268-fbdf-4dfd-9b8c-d4ccf052b969");

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        5,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_DeniedOrganizationAccess_ReturnsAccessDeniedWithoutRepositoryQuery()
    {
        var repository = new FakeOrganizationRepository(CreateOrganization());
        GetOrganizationByIdHandler handler = CreateHandler(null, repository);

        GetOrganizationByIdResult result = await handler.HandleAsync(
            UserId,
            OrganizationId);

        Assert.Equal(GetOrganizationByIdResultStatus.AccessDenied, result.Status);
        Assert.Null(result.Organization);
        Assert.Equal(0, repository.GetByIdAsyncCallCount);
    }

    [Fact]
    public async Task HandleAsync_AllowedAccessAndExistingOrganization_ReturnsAllMetadata()
    {
        Organization organization = CreateOrganization();
        organization.Deactivate();
        var repository = new FakeOrganizationRepository(organization);
        GetOrganizationByIdHandler handler = CreateHandler(
            OrganizationRole.Member,
            repository);

        GetOrganizationByIdResult result = await handler.HandleAsync(
            UserId,
            organization.Id);

        Assert.Equal(GetOrganizationByIdResultStatus.Succeeded, result.Status);
        OrganizationMetadataReadModel metadata = Assert.IsType<
            OrganizationMetadataReadModel>(result.Organization);
        Assert.Equal(organization.Id, metadata.Id);
        Assert.Equal("Enma Legal", metadata.Name);
        Assert.Equal("enma-legal", metadata.Slug);
        Assert.False(metadata.IsActive);
        Assert.Equal(CreatedAt, metadata.CreatedAt);
    }

    [Fact]
    public async Task HandleAsync_AllowedAccessAndMissingOrganization_ReturnsNotFound()
    {
        var repository = new FakeOrganizationRepository(null);
        GetOrganizationByIdHandler handler = CreateHandler(
            OrganizationRole.Owner,
            repository);

        GetOrganizationByIdResult result = await handler.HandleAsync(
            UserId,
            OrganizationId);

        Assert.Equal(GetOrganizationByIdResultStatus.NotFound, result.Status);
        Assert.Null(result.Organization);
        Assert.Equal(1, repository.GetByIdAsyncCallCount);
        Assert.Equal(OrganizationId, repository.ReceivedId);
    }

    [Fact]
    public async Task HandleAsync_AllowedContext_ForwardsIdentityTenantAndCancellation()
    {
        Organization organization = CreateOrganization();
        var repository = new FakeOrganizationRepository(organization);
        var accessLookup = new RecordingOrganizationAccessLookup(
            OrganizationRole.Administrator);
        var handler = new GetOrganizationByIdHandler(
            new OrganizationAccessAuthorization(accessLookup),
            repository);
        using var cancellationTokenSource = new CancellationTokenSource();

        await handler.HandleAsync(
            UserId,
            organization.Id,
            cancellationTokenSource.Token);

        Assert.Equal(1, accessLookup.CallCount);
        Assert.Equal(UserId, accessLookup.UserId);
        Assert.Equal(organization.Id, accessLookup.OrganizationId);
        Assert.Equal(
            cancellationTokenSource.Token,
            accessLookup.CancellationToken);
        Assert.Equal(1, repository.GetByIdAsyncCallCount);
        Assert.Equal(organization.Id, repository.ReceivedId);
        Assert.Equal(
            cancellationTokenSource.Token,
            repository.ReceivedCancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleAsync_EmptyContextId_ReturnsAccessDeniedWithoutQueries(
        bool emptyUserId)
    {
        var repository = new FakeOrganizationRepository(CreateOrganization());
        var accessLookup = new RecordingOrganizationAccessLookup(
            OrganizationRole.Owner);
        var handler = new GetOrganizationByIdHandler(
            new OrganizationAccessAuthorization(accessLookup),
            repository);

        GetOrganizationByIdResult result = await handler.HandleAsync(
            emptyUserId ? Guid.Empty : UserId,
            emptyUserId ? OrganizationId : Guid.Empty);

        Assert.Equal(GetOrganizationByIdResultStatus.AccessDenied, result.Status);
        Assert.Null(result.Organization);
        Assert.Equal(0, accessLookup.CallCount);
        Assert.Equal(0, repository.GetByIdAsyncCallCount);
    }

    [Fact]
    public void Constructor_NullAuthorization_ThrowsArgumentNullException()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new GetOrganizationByIdHandler(
                null!,
                new FakeOrganizationRepository(null)));

        Assert.Equal("organizationAccessAuthorization", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullRepository_ThrowsArgumentNullException()
    {
        var authorization = new OrganizationAccessAuthorization(
            new RecordingOrganizationAccessLookup(OrganizationRole.Owner));

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new GetOrganizationByIdHandler(authorization, null!));

        Assert.Equal("organizationRepository", exception.ParamName);
    }

    private static GetOrganizationByIdHandler CreateHandler(
        OrganizationRole? role,
        FakeOrganizationRepository repository)
    {
        return new GetOrganizationByIdHandler(
            new OrganizationAccessAuthorization(
                new RecordingOrganizationAccessLookup(role)),
            repository);
    }

    private static Organization CreateOrganization()
    {
        return new Organization("Enma Legal", "enma-legal", CreatedAt);
    }

    private sealed class RecordingOrganizationAccessLookup(
        OrganizationRole? role) : IOrganizationAccessLookup
    {
        public int CallCount { get; private set; }

        public Guid UserId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            UserId = userId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;

            return Task.FromResult(role);
        }
    }

    private sealed class FakeOrganizationRepository(Organization? organization)
        : IOrganizationRepository
    {
        public int GetByIdAsyncCallCount { get; private set; }

        public Guid ReceivedId { get; private set; }

        public CancellationToken ReceivedCancellationToken { get; private set; }

        public Task<Organization?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            GetByIdAsyncCallCount++;
            ReceivedId = id;
            ReceivedCancellationToken = cancellationToken;

            return Task.FromResult(organization);
        }

        public Task<bool> ExistsBySlugAsync(
            string slug,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "ExistsBySlugAsync must not be called by retrieval tests.");
        }

        public Task AddAsync(
            Organization organizationToAdd,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "AddAsync must not be called by retrieval tests.");
        }
    }
}
