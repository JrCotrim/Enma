using Enma.Application.Abstractions;
using Enma.Application.Organizations;
using Enma.Application.Organizations.Create;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Organizations;

public sealed class CreateOrganizationHandlerTests
{
    private static readonly DateTimeOffset FixedUtcNow =
        new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_WithValidCommand_CreatesOrganization()
    {
        var repository = new FakeOrganizationRepository();
        var handler = CreateHandler(repository);
        var command = new CreateOrganizationCommand("Enma Advocacia", "enma-advocacia");

        CreateOrganizationResult result = await handler.HandleAsync(command);

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(repository.AddedOrganization?.Id, result.Id);
        Assert.Equal("Enma Advocacia", result.Name);
        Assert.Equal("enma-advocacia", result.Slug);
        Assert.True(result.IsActive);
        Assert.Equal(FixedUtcNow, result.CreatedAt);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_UsesCurrentUtcTimeFromTimeProvider()
    {
        var repository = new FakeOrganizationRepository();
        var handler = CreateHandler(repository);

        CreateOrganizationResult result = await handler.HandleAsync(CreateValidCommand());

        Assert.Equal(FixedUtcNow, repository.AddedOrganization?.CreatedAt);
        Assert.Equal(FixedUtcNow, result.CreatedAt);
    }

    [Fact]
    public async Task HandleAsync_WithNameContainingWhitespace_NormalizesName()
    {
        var handler = CreateHandler();
        var command = new CreateOrganizationCommand("  Enma Advocacia  ", "enma-advocacia");

        CreateOrganizationResult result = await handler.HandleAsync(command);

        Assert.Equal("Enma Advocacia", result.Name);
    }

    [Fact]
    public async Task HandleAsync_WithUppercaseSlug_NormalizesSlug()
    {
        var handler = CreateHandler();
        var command = new CreateOrganizationCommand("Enma Advocacia", "  ENMA-ADVOCACIA  ");

        CreateOrganizationResult result = await handler.HandleAsync(command);

        Assert.Equal("enma-advocacia", result.Slug);
    }

    [Fact]
    public async Task HandleAsync_ChecksDuplicateUsingNormalizedSlug()
    {
        var repository = new FakeOrganizationRepository();
        var handler = CreateHandler(repository);
        var command = new CreateOrganizationCommand("Enma Advocacia", "  ENMA-ADVOCACIA  ");

        await handler.HandleAsync(command);

        Assert.Equal("enma-advocacia", repository.CheckedSlug);
    }

    [Fact]
    public async Task HandleAsync_WhenSlugDoesNotExist_AddsOrganization()
    {
        var repository = new FakeOrganizationRepository();
        var handler = CreateHandler(repository);

        await handler.HandleAsync(CreateValidCommand());

        Assert.True(repository.AddAsyncCalled);
        Assert.NotNull(repository.AddedOrganization);
    }

    [Fact]
    public async Task HandleAsync_WhenSlugDoesNotExist_SavesChanges()
    {
        var unitOfWork = new FakeUnitOfWork();
        var handler = CreateHandler(unitOfWork: unitOfWork);

        await handler.HandleAsync(CreateValidCommand());

        Assert.True(unitOfWork.SaveChangesAsyncCalled);
    }

    [Fact]
    public async Task HandleAsync_WhenSlugAlreadyExists_ThrowsOrganizationSlugAlreadyExistsException()
    {
        var repository = new FakeOrganizationRepository { SlugExists = true };
        var handler = CreateHandler(repository);
        var command = new CreateOrganizationCommand("Enma Advocacia", "ENMA-ADVOCACIA");

        var exception = await Assert.ThrowsAsync<OrganizationSlugAlreadyExistsException>(
            () => handler.HandleAsync(command));

        Assert.Equal("enma-advocacia", exception.Slug);
        Assert.Equal(
            "An organization with the slug 'enma-advocacia' already exists.",
            exception.Message);
    }

    [Fact]
    public async Task HandleAsync_WhenSlugAlreadyExists_DoesNotAddOrganization()
    {
        var repository = new FakeOrganizationRepository { SlugExists = true };
        var handler = CreateHandler(repository);

        await Assert.ThrowsAsync<OrganizationSlugAlreadyExistsException>(
            () => handler.HandleAsync(CreateValidCommand()));

        Assert.False(repository.AddAsyncCalled);
        Assert.Null(repository.AddedOrganization);
    }

    [Fact]
    public async Task HandleAsync_WhenSlugAlreadyExists_DoesNotSaveChanges()
    {
        var repository = new FakeOrganizationRepository { SlugExists = true };
        var unitOfWork = new FakeUnitOfWork();
        var handler = CreateHandler(repository, unitOfWork);

        await Assert.ThrowsAsync<OrganizationSlugAlreadyExistsException>(
            () => handler.HandleAsync(CreateValidCommand()));

        Assert.False(unitOfWork.SaveChangesAsyncCalled);
    }

    [Fact]
    public async Task HandleAsync_WithInvalidName_PropagatesDomainException()
    {
        var repository = new FakeOrganizationRepository();
        var unitOfWork = new FakeUnitOfWork();
        var handler = CreateHandler(repository, unitOfWork);
        var command = new CreateOrganizationCommand("   ", "enma-advocacia");

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(command));

        Assert.Contains(OrganizationErrors.NameRequired, exception.Message);
        Assert.False(repository.ExistsBySlugAsyncCalled);
        Assert.False(repository.AddAsyncCalled);
        Assert.False(unitOfWork.SaveChangesAsyncCalled);
    }

    [Fact]
    public async Task HandleAsync_WithInvalidSlug_PropagatesDomainException()
    {
        var repository = new FakeOrganizationRepository();
        var unitOfWork = new FakeUnitOfWork();
        var handler = CreateHandler(repository, unitOfWork);
        var command = new CreateOrganizationCommand("Enma Advocacia", "enma_advocacia");

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(command));

        Assert.Contains(OrganizationErrors.SlugInvalidFormat, exception.Message);
        Assert.False(repository.ExistsBySlugAsyncCalled);
        Assert.False(repository.AddAsyncCalled);
        Assert.False(unitOfWork.SaveChangesAsyncCalled);
    }

    [Fact]
    public async Task HandleAsync_ForwardsCancellationTokenToRepositoryAndUnitOfWork()
    {
        var repository = new FakeOrganizationRepository();
        var unitOfWork = new FakeUnitOfWork();
        var handler = CreateHandler(repository, unitOfWork);
        using var cancellationTokenSource = new CancellationTokenSource();
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        await handler.HandleAsync(CreateValidCommand(), cancellationToken);

        Assert.Equal(cancellationToken, repository.ExistsCancellationToken);
        Assert.Equal(cancellationToken, repository.AddCancellationToken);
        Assert.Equal(cancellationToken, unitOfWork.CancellationToken);
    }

    [Fact]
    public async Task HandleAsync_WithNullCommand_ThrowsArgumentNullException()
    {
        var handler = CreateHandler();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => handler.HandleAsync(null!));

        Assert.Equal("command", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithNullOrganizationRepository_ThrowsArgumentNullException()
    {
        var unitOfWork = new FakeUnitOfWork();
        var timeProvider = new FixedTimeProvider(FixedUtcNow);

        var exception = Assert.Throws<ArgumentNullException>(
            () => new CreateOrganizationHandler(null!, unitOfWork, timeProvider));

        Assert.Equal("organizationRepository", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithNullUnitOfWork_ThrowsArgumentNullException()
    {
        var repository = new FakeOrganizationRepository();
        var timeProvider = new FixedTimeProvider(FixedUtcNow);

        var exception = Assert.Throws<ArgumentNullException>(
            () => new CreateOrganizationHandler(repository, null!, timeProvider));

        Assert.Equal("unitOfWork", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithNullTimeProvider_ThrowsArgumentNullException()
    {
        var repository = new FakeOrganizationRepository();
        var unitOfWork = new FakeUnitOfWork();

        var exception = Assert.Throws<ArgumentNullException>(
            () => new CreateOrganizationHandler(repository, unitOfWork, null!));

        Assert.Equal("timeProvider", exception.ParamName);
    }

    private static CreateOrganizationCommand CreateValidCommand()
    {
        return new CreateOrganizationCommand("Enma Advocacia", "enma-advocacia");
    }

    private static CreateOrganizationHandler CreateHandler(
        FakeOrganizationRepository? repository = null,
        FakeUnitOfWork? unitOfWork = null)
    {
        return new CreateOrganizationHandler(
            repository ?? new FakeOrganizationRepository(),
            unitOfWork ?? new FakeUnitOfWork(),
            new FixedTimeProvider(FixedUtcNow));
    }

    private sealed class FakeOrganizationRepository : IOrganizationRepository
    {
        public bool SlugExists { get; init; }

        public string? CheckedSlug { get; private set; }

        public Organization? AddedOrganization { get; private set; }

        public bool ExistsBySlugAsyncCalled { get; private set; }

        public bool AddAsyncCalled { get; private set; }

        public CancellationToken ExistsCancellationToken { get; private set; }

        public CancellationToken AddCancellationToken { get; private set; }

        public Task<Organization?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "GetByIdAsync must not be called by creation tests.");
        }

        public Task<bool> ExistsBySlugAsync(
            string slug,
            CancellationToken cancellationToken = default)
        {
            ExistsBySlugAsyncCalled = true;
            CheckedSlug = slug;
            ExistsCancellationToken = cancellationToken;

            return Task.FromResult(SlugExists);
        }

        public Task AddAsync(
            Organization organization,
            CancellationToken cancellationToken = default)
        {
            AddAsyncCalled = true;
            AddedOrganization = organization;
            AddCancellationToken = cancellationToken;

            return Task.CompletedTask;
        }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public bool SaveChangesAsyncCalled { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesAsyncCalled = true;
            CancellationToken = cancellationToken;

            return Task.FromResult(1);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}
