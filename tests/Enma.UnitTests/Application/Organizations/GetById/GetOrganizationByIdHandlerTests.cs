using Enma.Application.Organizations;
using Enma.Application.Organizations.GetById;
using Enma.Application.Validation;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Organizations.GetById;

public sealed class GetOrganizationByIdHandlerTests
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        5,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_WithExistingOrganization_ReturnsOrganization()
    {
        Organization organization = CreateOrganization();
        var repository = new FakeOrganizationRepository(organization);
        var handler = new GetOrganizationByIdHandler(repository);

        GetOrganizationByIdResult result = await handler.HandleAsync(organization.Id);

        Assert.NotNull(result);
        Assert.Equal(organization.Id, result.Id);
    }

    [Fact]
    public async Task HandleAsync_WithExistingOrganization_MapsAllFields()
    {
        Organization organization = CreateOrganization();
        organization.Deactivate();
        var repository = new FakeOrganizationRepository(organization);
        var handler = new GetOrganizationByIdHandler(repository);

        GetOrganizationByIdResult result = await handler.HandleAsync(organization.Id);

        Assert.Equal(organization.Id, result.Id);
        Assert.Equal("Enma Legal", result.Name);
        Assert.Equal("enma-legal", result.Slug);
        Assert.False(result.IsActive);
        Assert.Equal(CreatedAt, result.CreatedAt);
    }

    [Fact]
    public async Task HandleAsync_WithExistingOrganization_ForwardsCancellationToken()
    {
        Organization organization = CreateOrganization();
        var repository = new FakeOrganizationRepository(organization);
        var handler = new GetOrganizationByIdHandler(repository);
        using var cancellationTokenSource = new CancellationTokenSource();
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        await handler.HandleAsync(organization.Id, cancellationToken);

        Assert.Equal(organization.Id, repository.ReceivedId);
        Assert.Equal(cancellationToken, repository.ReceivedCancellationToken);
    }

    [Fact]
    public async Task HandleAsync_WithMissingOrganization_ThrowsOrganizationNotFoundException()
    {
        Guid organizationId = Guid.Parse("2344ce14-f31f-4731-ac01-c54933fc8941");
        var repository = new FakeOrganizationRepository(null);
        var handler = new GetOrganizationByIdHandler(repository);

        OrganizationNotFoundException exception =
            await Assert.ThrowsAsync<OrganizationNotFoundException>(
                () => handler.HandleAsync(organizationId));

        Assert.Equal(organizationId, exception.OrganizationId);
        Assert.Equal(
            $"Organization with id '{organizationId}' was not found.",
            exception.Message);
        Assert.True(repository.GetByIdAsyncCalled);
        Assert.Equal(1, repository.GetByIdAsyncCallCount);
        Assert.Equal(organizationId, repository.ReceivedId);
    }

    [Fact]
    public async Task HandleAsync_WithEmptyId_ThrowsRequestValidationExceptionBeforeRepositoryAccess()
    {
        var repository = new FakeOrganizationRepository(CreateOrganization());
        var handler = new GetOrganizationByIdHandler(repository);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => handler.HandleAsync(Guid.Empty));

        Assert.Equal("Organization id cannot be empty.", exception.Message);
        Assert.False(repository.GetByIdAsyncCalled);
    }

    [Fact]
    public void Constructor_WithNullRepository_ThrowsArgumentNullException()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new GetOrganizationByIdHandler(null!));

        Assert.Equal("organizationRepository", exception.ParamName);
    }

    private static Organization CreateOrganization()
    {
        return new Organization("Enma Legal", "enma-legal", CreatedAt);
    }

    private sealed class FakeOrganizationRepository(Organization? organization)
        : IOrganizationRepository
    {
        public Organization? OrganizationToReturn { get; } = organization;

        public bool GetByIdAsyncCalled { get; private set; }

        public int GetByIdAsyncCallCount { get; private set; }

        public Guid ReceivedId { get; private set; }

        public CancellationToken ReceivedCancellationToken { get; private set; }

        public Task<Organization?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            GetByIdAsyncCalled = true;
            GetByIdAsyncCallCount++;
            ReceivedId = id;
            ReceivedCancellationToken = cancellationToken;

            return Task.FromResult(OrganizationToReturn);
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
