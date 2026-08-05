using Enma.Application.Abstractions;
using Enma.Application.Onboarding.RegisterOrganizationOwner;
using Enma.Application.Organizations;
using Enma.Application.Organizations.Create;
using Enma.Application.Users;
using Enma.Domain.Organizations;
using Enma.Domain.Users;

namespace Enma.UnitTests.Application.Onboarding;

public sealed class RegisterOrganizationOwnerHandlerTests
{
    private static readonly DateTimeOffset FixedUtcNow =
        new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_WithValidCommand_ReturnsCreatedIdentifiers()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();

        RegisterOrganizationOwnerResult result = await handler.HandleAsync(CreateValidCommand());

        Assert.NotEqual(Guid.Empty, result.OrganizationId);
        Assert.NotEqual(Guid.Empty, result.UserId);
        Assert.NotEqual(Guid.Empty, result.MembershipId);
        Assert.Equal(3, new[]
        {
            result.OrganizationId,
            result.UserId,
            result.MembershipId
        }.Distinct().Count());
        Assert.Equal(dependencies.OrganizationRepository.AddedOrganization?.Id, result.OrganizationId);
        Assert.Equal(dependencies.UserRepository.AddedUser?.Id, result.UserId);
        Assert.Equal(dependencies.MembershipRepository.AddedMembership?.Id, result.MembershipId);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_ReturnsNormalizedValues()
    {
        var handler = new TestDependencies().CreateHandler();
        var command = new RegisterOrganizationOwnerCommand(
            "  Enma Advocacia  ",
            "  ENMA-ADVOCACIA  ",
            "  Ana Silva  ",
            "  ANA.SILVA@EXAMPLE.COM  ");

        RegisterOrganizationOwnerResult result = await handler.HandleAsync(command);

        Assert.Equal("Enma Advocacia", result.OrganizationName);
        Assert.Equal("enma-advocacia", result.OrganizationSlug);
        Assert.Equal("Ana Silva", result.UserName);
        Assert.Equal("ana.silva@example.com", result.UserEmail);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_ReturnsOwnerRole()
    {
        var handler = new TestDependencies().CreateHandler();

        RegisterOrganizationOwnerResult result = await handler.HandleAsync(CreateValidCommand());

        Assert.Equal(OrganizationRole.Owner, result.Role);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_UsesSingleTimestampForAllEntities()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();

        RegisterOrganizationOwnerResult result = await handler.HandleAsync(CreateValidCommand());

        Assert.Equal(FixedUtcNow, dependencies.OrganizationRepository.AddedOrganization?.CreatedAt);
        Assert.Equal(FixedUtcNow, dependencies.UserRepository.AddedUser?.CreatedAt);
        Assert.Equal(FixedUtcNow, dependencies.MembershipRepository.AddedMembership?.CreatedAt);
        Assert.Equal(FixedUtcNow, result.CreatedAt);
        Assert.Equal(1, dependencies.TimeProvider.GetUtcNowCallCount);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_CreatesMembershipForCreatedOrganizationAndUser()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();

        await handler.HandleAsync(CreateValidCommand());

        Organization organization = Assert.IsType<Organization>(
            dependencies.OrganizationRepository.AddedOrganization);
        User user = Assert.IsType<User>(dependencies.UserRepository.AddedUser);
        OrganizationMembership membership = Assert.IsType<OrganizationMembership>(
            dependencies.MembershipRepository.AddedMembership);
        Assert.Equal(organization.Id, membership.OrganizationId);
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(OrganizationRole.Owner, membership.Role);
        Assert.True(membership.IsActive);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_AddsAllEntitiesAndSavesOnce()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();

        await handler.HandleAsync(CreateValidCommand());

        Assert.Equal(1, dependencies.OrganizationRepository.AddCallCount);
        Assert.Equal(1, dependencies.UserRepository.AddCallCount);
        Assert.Equal(1, dependencies.MembershipRepository.AddCallCount);
        Assert.Equal(1, dependencies.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_PerformsOperationsInRequiredOrder()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();

        await handler.HandleAsync(CreateValidCommand());

        Assert.Equal(
            new[]
            {
                "organization-exists",
                "user-exists",
                "organization-add",
                "user-add",
                "membership-add",
                "save"
            },
            dependencies.Operations);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_ForwardsCancellationToken()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();
        using var cancellationTokenSource = new CancellationTokenSource();
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        await handler.HandleAsync(CreateValidCommand(), cancellationToken);

        Assert.Equal(cancellationToken, dependencies.OrganizationRepository.ExistsCancellationToken);
        Assert.Equal(cancellationToken, dependencies.UserRepository.ExistsCancellationToken);
        Assert.Equal(cancellationToken, dependencies.OrganizationRepository.AddCancellationToken);
        Assert.Equal(cancellationToken, dependencies.UserRepository.AddCancellationToken);
        Assert.Equal(cancellationToken, dependencies.MembershipRepository.AddCancellationToken);
        Assert.Equal(cancellationToken, dependencies.UnitOfWork.CancellationToken);
    }

    [Fact]
    public async Task HandleAsync_WithInvalidOrganizationName_ThrowsBeforeRepositoryAccess()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();
        RegisterOrganizationOwnerCommand command = CreateValidCommand() with
        {
            OrganizationName = "   "
        };

        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command));

        Assert.Empty(dependencies.Operations);
    }

    [Fact]
    public async Task HandleAsync_WithInvalidOrganizationSlug_ThrowsBeforeRepositoryAccess()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();
        RegisterOrganizationOwnerCommand command = CreateValidCommand() with
        {
            OrganizationSlug = "enma_advocacia"
        };

        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command));

        Assert.Empty(dependencies.Operations);
    }

    [Fact]
    public async Task HandleAsync_WithInvalidOwnerName_ThrowsBeforeRepositoryAccess()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();
        RegisterOrganizationOwnerCommand command = CreateValidCommand() with
        {
            OwnerName = "   "
        };

        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command));

        Assert.Empty(dependencies.Operations);
    }

    [Fact]
    public async Task HandleAsync_WithInvalidOwnerEmail_ThrowsBeforeRepositoryAccess()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();
        RegisterOrganizationOwnerCommand command = CreateValidCommand() with
        {
            OwnerEmail = "invalid email"
        };

        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command));

        Assert.Empty(dependencies.Operations);
    }

    [Fact]
    public async Task HandleAsync_WithExistingOrganizationSlug_ThrowsOrganizationSlugAlreadyExistsException()
    {
        var dependencies = new TestDependencies();
        dependencies.OrganizationRepository.SlugExists = true;
        var handler = dependencies.CreateHandler();
        RegisterOrganizationOwnerCommand command = CreateValidCommand() with
        {
            OrganizationSlug = "  ENMA-ADVOCACIA  "
        };

        var exception = await Assert.ThrowsAsync<OrganizationSlugAlreadyExistsException>(
            () => handler.HandleAsync(command));

        Assert.Equal("enma-advocacia", exception.Slug);
        Assert.Equal(0, dependencies.UserRepository.ExistsCallCount);
        Assert.Equal(0, dependencies.OrganizationRepository.AddCallCount);
        Assert.Equal(0, dependencies.UserRepository.AddCallCount);
        Assert.Equal(0, dependencies.MembershipRepository.AddCallCount);
        Assert.Equal(0, dependencies.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(new[] { "organization-exists" }, dependencies.Operations);
    }

    [Fact]
    public async Task HandleAsync_WithExistingUserEmail_ThrowsUserEmailAlreadyExistsException()
    {
        var dependencies = new TestDependencies();
        dependencies.UserRepository.EmailExists = true;
        var handler = dependencies.CreateHandler();
        RegisterOrganizationOwnerCommand command = CreateValidCommand() with
        {
            OwnerEmail = "  OWNER@EXAMPLE.COM  "
        };

        var exception = await Assert.ThrowsAsync<UserEmailAlreadyExistsException>(
            () => handler.HandleAsync(command));

        Assert.Equal("owner@example.com", exception.Email);
        Assert.Equal("A user with the provided email already exists.", exception.Message);
        Assert.Equal(0, dependencies.OrganizationRepository.AddCallCount);
        Assert.Equal(0, dependencies.UserRepository.AddCallCount);
        Assert.Equal(0, dependencies.MembershipRepository.AddCallCount);
        Assert.Equal(0, dependencies.UnitOfWork.SaveChangesCallCount);
        Assert.Equal(
            new[] { "organization-exists", "user-exists" },
            dependencies.Operations);
    }

    [Fact]
    public async Task HandleAsync_UsesNormalizedValuesForDuplicateChecks()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();
        var command = new RegisterOrganizationOwnerCommand(
            "Enma Advocacia",
            "  ENMA-ADVOCACIA  ",
            "Ana Silva",
            "  OWNER@EXAMPLE.COM  ");

        await handler.HandleAsync(command);

        Assert.Equal("enma-advocacia", dependencies.OrganizationRepository.CheckedSlug);
        Assert.Equal("owner@example.com", dependencies.UserRepository.CheckedEmail);
    }

    [Fact]
    public async Task HandleAsync_WithNullCommand_ThrowsArgumentNullException()
    {
        var dependencies = new TestDependencies();
        var handler = dependencies.CreateHandler();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => handler.HandleAsync(null!));

        Assert.Equal("command", exception.ParamName);
        Assert.Empty(dependencies.Operations);
        Assert.Equal(0, dependencies.TimeProvider.GetUtcNowCallCount);
    }

    [Fact]
    public void Constructor_WithNullRepositoryDependency_ThrowsArgumentNullException()
    {
        var dependencies = new TestDependencies();

        var organizationException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                null!,
                dependencies.UserRepository,
                dependencies.MembershipRepository,
                dependencies.UnitOfWork,
                dependencies.TimeProvider));
        var userException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                dependencies.OrganizationRepository,
                null!,
                dependencies.MembershipRepository,
                dependencies.UnitOfWork,
                dependencies.TimeProvider));
        var membershipException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                dependencies.OrganizationRepository,
                dependencies.UserRepository,
                null!,
                dependencies.UnitOfWork,
                dependencies.TimeProvider));

        Assert.Equal("organizationRepository", organizationException.ParamName);
        Assert.Equal("userRepository", userException.ParamName);
        Assert.Equal("membershipRepository", membershipException.ParamName);
    }

    [Fact]
    public void Constructor_WithNullServiceDependency_ThrowsArgumentNullException()
    {
        var dependencies = new TestDependencies();

        var unitOfWorkException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                dependencies.OrganizationRepository,
                dependencies.UserRepository,
                dependencies.MembershipRepository,
                null!,
                dependencies.TimeProvider));
        var timeProviderException = Assert.Throws<ArgumentNullException>(
            () => new RegisterOrganizationOwnerHandler(
                dependencies.OrganizationRepository,
                dependencies.UserRepository,
                dependencies.MembershipRepository,
                dependencies.UnitOfWork,
                null!));

        Assert.Equal("unitOfWork", unitOfWorkException.ParamName);
        Assert.Equal("timeProvider", timeProviderException.ParamName);
    }

    private static RegisterOrganizationOwnerCommand CreateValidCommand()
    {
        return new RegisterOrganizationOwnerCommand(
            "Enma Advocacia",
            "enma-advocacia",
            "Ana Silva",
            "owner@example.com");
    }

    private sealed class TestDependencies
    {
        public TestDependencies()
        {
            OrganizationRepository = new FakeOrganizationRepository(Operations);
            UserRepository = new FakeUserRepository(Operations);
            MembershipRepository = new FakeOrganizationMembershipRepository(Operations);
            UnitOfWork = new FakeUnitOfWork(Operations);
            TimeProvider = new FixedTimeProvider(FixedUtcNow);
        }

        public List<string> Operations { get; } = [];

        public FakeOrganizationRepository OrganizationRepository { get; }

        public FakeUserRepository UserRepository { get; }

        public FakeOrganizationMembershipRepository MembershipRepository { get; }

        public FakeUnitOfWork UnitOfWork { get; }

        public FixedTimeProvider TimeProvider { get; }

        public RegisterOrganizationOwnerHandler CreateHandler()
        {
            return new RegisterOrganizationOwnerHandler(
                OrganizationRepository,
                UserRepository,
                MembershipRepository,
                UnitOfWork,
                TimeProvider);
        }
    }

    private sealed class FakeOrganizationRepository(List<string> operations)
        : IOrganizationRepository
    {
        public bool SlugExists { get; set; }

        public string? CheckedSlug { get; private set; }

        public Organization? AddedOrganization { get; private set; }

        public int ExistsCallCount { get; private set; }

        public int AddCallCount { get; private set; }

        public CancellationToken ExistsCancellationToken { get; private set; }

        public CancellationToken AddCancellationToken { get; private set; }

        public Task<Organization?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "GetByIdAsync must not be called by onboarding tests.");
        }

        public Task<bool> ExistsBySlugAsync(
            string slug,
            CancellationToken cancellationToken = default)
        {
            operations.Add("organization-exists");
            ExistsCallCount++;
            CheckedSlug = slug;
            ExistsCancellationToken = cancellationToken;

            return Task.FromResult(SlugExists);
        }

        public Task AddAsync(
            Organization organization,
            CancellationToken cancellationToken = default)
        {
            operations.Add("organization-add");
            AddCallCount++;
            AddedOrganization = organization;
            AddCancellationToken = cancellationToken;

            return Task.CompletedTask;
        }
    }

    private sealed class FakeUserRepository(List<string> operations) : IUserRepository
    {
        public bool EmailExists { get; set; }

        public string? CheckedEmail { get; private set; }

        public User? AddedUser { get; private set; }

        public int ExistsCallCount { get; private set; }

        public int AddCallCount { get; private set; }

        public CancellationToken ExistsCancellationToken { get; private set; }

        public CancellationToken AddCancellationToken { get; private set; }

        public Task<bool> ExistsByEmailAsync(
            string email,
            CancellationToken cancellationToken = default)
        {
            operations.Add("user-exists");
            ExistsCallCount++;
            CheckedEmail = email;
            ExistsCancellationToken = cancellationToken;

            return Task.FromResult(EmailExists);
        }

        public Task AddAsync(User user, CancellationToken cancellationToken = default)
        {
            operations.Add("user-add");
            AddCallCount++;
            AddedUser = user;
            AddCancellationToken = cancellationToken;

            return Task.CompletedTask;
        }
    }

    private sealed class FakeOrganizationMembershipRepository(List<string> operations)
        : IOrganizationMembershipRepository
    {
        public OrganizationMembership? AddedMembership { get; private set; }

        public int AddCallCount { get; private set; }

        public CancellationToken AddCancellationToken { get; private set; }

        public Task AddAsync(
            OrganizationMembership membership,
            CancellationToken cancellationToken = default)
        {
            operations.Add("membership-add");
            AddCallCount++;
            AddedMembership = membership;
            AddCancellationToken = cancellationToken;

            return Task.CompletedTask;
        }
    }

    private sealed class FakeUnitOfWork(List<string> operations) : IUnitOfWork
    {
        public int SaveChangesCallCount { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            operations.Add("save");
            SaveChangesCallCount++;
            CancellationToken = cancellationToken;

            return Task.FromResult(3);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public int GetUtcNowCallCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            GetUtcNowCallCount++;

            return utcNow;
        }
    }
}
