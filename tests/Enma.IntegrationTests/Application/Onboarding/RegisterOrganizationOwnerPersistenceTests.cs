using Enma.Application.Abstractions;
using Enma.Application.Onboarding.RegisterOrganizationOwner;
using Enma.Application.Organizations;
using Enma.Application.Organizations.Create;
using Enma.Application.Users;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Repositories;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Enma.IntegrationTests.Application.Onboarding;

[Collection(PostgreSqlCollection.Name)]
public sealed class RegisterOrganizationOwnerPersistenceTests(PostgreSqlFixture fixture)
    : IAsyncLifetime
{
    private const string SafeDuplicateEmailMessage =
        "A user with the provided email already exists.";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        1,
        2,
        3,
        4,
        5,
        TimeSpan.Zero);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_PersistsOrganizationUserAndOwnerMembership()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        RegisterOrganizationOwnerHandler handler = CreateHandler(dbContext);

        RegisterOrganizationOwnerResult result = await handler.HandleAsync(
            CreateCommand());

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Organization organization = await verificationContext.Organizations
            .AsNoTracking()
            .SingleAsync();
        User user = await verificationContext.Users
            .AsNoTracking()
            .SingleAsync();
        OrganizationMembership membership =
            await verificationContext.OrganizationMemberships
                .AsNoTracking()
                .SingleAsync();

        Assert.Equal(organization.Id, result.OrganizationId);
        Assert.Equal("Enma Legal", organization.Name);
        Assert.Equal("enma-legal", organization.Slug);
        Assert.Equal(organization.Name, result.OrganizationName);
        Assert.Equal(organization.Slug, result.OrganizationSlug);
        Assert.True(organization.IsActive);
        Assert.Equal(CreatedAt, organization.CreatedAt);

        Assert.Equal(user.Id, result.UserId);
        Assert.Equal("Ana Silva", user.Name);
        Assert.Equal("owner@example.com", user.Email);
        Assert.Equal(user.Name, result.UserName);
        Assert.Equal(user.Email, result.UserEmail);
        Assert.True(user.IsActive);
        Assert.Equal(CreatedAt, user.CreatedAt);

        Assert.Equal(membership.Id, result.MembershipId);
        Assert.Equal(organization.Id, membership.OrganizationId);
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(OrganizationRole.Owner, membership.Role);
        Assert.Equal(membership.Role, result.Role);
        Assert.True(membership.IsActive);
        Assert.Equal(CreatedAt, membership.CreatedAt);
        Assert.Equal(membership.CreatedAt, result.CreatedAt);
    }

    [Fact]
    public async Task HandleAsync_WithExistingSlug_PrecheckPreventsAllWrites()
    {
        Organization seededOrganization = new(
            "Existing Legal",
            "enma-legal",
            CreatedAt.AddMinutes(-1));
        await SeedAsync(seededOrganization);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        RegisterOrganizationOwnerHandler handler = CreateHandler(dbContext);

        OrganizationSlugAlreadyExistsException exception =
            await Assert.ThrowsAsync<OrganizationSlugAlreadyExistsException>(
                () => handler.HandleAsync(CreateCommand()));

        Assert.Equal("enma-legal", exception.Slug);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Organization persistedOrganization = await verificationContext.Organizations
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(seededOrganization.Id, persistedOrganization.Id);
        Assert.Empty(await verificationContext.Users.AsNoTracking().ToListAsync());
        Assert.Empty(
            await verificationContext.OrganizationMemberships
                .AsNoTracking()
                .ToListAsync());
    }

    [Fact]
    public async Task HandleAsync_WithExistingEmail_PrecheckPreventsAllWrites()
    {
        User seededUser = new(
            "Existing User",
            "owner@example.com",
            CreatedAt.AddMinutes(-1));
        await SeedAsync(seededUser);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        RegisterOrganizationOwnerHandler handler = CreateHandler(dbContext);

        UserEmailAlreadyExistsException exception =
            await Assert.ThrowsAsync<UserEmailAlreadyExistsException>(
                () => handler.HandleAsync(CreateCommand()));

        Assert.Equal("owner@example.com", exception.Email);
        Assert.Equal(SafeDuplicateEmailMessage, exception.Message);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        User persistedUser = await verificationContext.Users
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(seededUser.Id, persistedUser.Id);
        Assert.Empty(
            await verificationContext.Organizations.AsNoTracking().ToListAsync());
        Assert.Empty(
            await verificationContext.OrganizationMemberships
                .AsNoTracking()
                .ToListAsync());
    }

    [Fact]
    public async Task HandleAsync_WithStaleSlugPrecheck_TranslatesConstraintAndRollsBackAllNewEntities()
    {
        Organization seededOrganization = new(
            "Concurrent Legal",
            "enma-legal",
            CreatedAt.AddMinutes(-1));
        await SeedAsync(seededOrganization);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var staleRepository = new StaleOrganizationRepository(
            new OrganizationRepository(dbContext));
        RegisterOrganizationOwnerHandler handler = CreateHandler(
            dbContext,
            organizationRepository: staleRepository);

        OrganizationSlugAlreadyExistsException exception =
            await Assert.ThrowsAsync<OrganizationSlugAlreadyExistsException>(
                () => handler.HandleAsync(CreateCommand()));

        Assert.Equal("enma-legal", exception.Slug);
        DbUpdateException dbUpdateException =
            Assert.IsType<DbUpdateException>(exception.InnerException);
        PostgresException postgresException =
            Assert.IsType<PostgresException>(dbUpdateException.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal("ux_organizations_slug", postgresException.ConstraintName);

        await AssertOnlySeededOrganizationRemainsAsync(seededOrganization.Id);
    }

    [Fact]
    public async Task HandleAsync_WithStaleEmailPrecheck_TranslatesConstraintAndRollsBackAllNewEntities()
    {
        User seededUser = new(
            "Concurrent User",
            "owner@example.com",
            CreatedAt.AddMinutes(-1));
        await SeedAsync(seededUser);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var staleRepository = new StaleUserRepository(
            new UserRepository(dbContext));
        RegisterOrganizationOwnerHandler handler = CreateHandler(
            dbContext,
            userRepository: staleRepository);

        UserEmailAlreadyExistsException exception =
            await Assert.ThrowsAsync<UserEmailAlreadyExistsException>(
                () => handler.HandleAsync(CreateCommand()));

        Assert.Equal("owner@example.com", exception.Email);
        Assert.Equal(SafeDuplicateEmailMessage, exception.Message);
        DbUpdateException dbUpdateException =
            Assert.IsType<DbUpdateException>(exception.InnerException);
        PostgresException postgresException =
            Assert.IsType<PostgresException>(dbUpdateException.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal("ux_users_email", postgresException.ConstraintName);

        await AssertOnlySeededUserRemainsAsync(seededUser.Id);
    }

    private static RegisterOrganizationOwnerCommand CreateCommand()
    {
        return new RegisterOrganizationOwnerCommand(
            "  Enma Legal  ",
            "  ENMA-LEGAL  ",
            "  Ana Silva  ",
            "  OWNER@EXAMPLE.COM  ");
    }

    private static RegisterOrganizationOwnerHandler CreateHandler(
        EnmaDbContext dbContext,
        IOrganizationRepository? organizationRepository = null,
        IUserRepository? userRepository = null)
    {
        return new RegisterOrganizationOwnerHandler(
            organizationRepository ?? new OrganizationRepository(dbContext),
            userRepository ?? new UserRepository(dbContext),
            new OrganizationMembershipRepository(dbContext),
            dbContext,
            new FixedTimeProvider(CreatedAt));
    }

    private async Task SeedAsync(object entity)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Add(entity);
        await dbContext.SaveChangesAsync();
    }

    private async Task AssertOnlySeededOrganizationRemainsAsync(Guid organizationId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = await dbContext.Organizations
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(organizationId, organization.Id);
        Assert.Empty(await dbContext.Users.AsNoTracking().ToListAsync());
        Assert.Empty(
            await dbContext.OrganizationMemberships.AsNoTracking().ToListAsync());
    }

    private async Task AssertOnlySeededUserRemainsAsync(Guid userId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = await dbContext.Users
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(userId, user.Id);
        Assert.Empty(await dbContext.Organizations.AsNoTracking().ToListAsync());
        Assert.Empty(
            await dbContext.OrganizationMemberships.AsNoTracking().ToListAsync());
    }

    private sealed class StaleOrganizationRepository(
        OrganizationRepository repository) : IOrganizationRepository
    {
        public Task<Organization?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "GetByIdAsync must not be called by this onboarding test.");
        }

        public Task<bool> ExistsBySlugAsync(
            string slug,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task AddAsync(
            Organization organization,
            CancellationToken cancellationToken = default)
        {
            return repository.AddAsync(organization, cancellationToken);
        }
    }

    private sealed class StaleUserRepository(UserRepository repository)
        : IUserRepository
    {
        public Task<bool> ExistsByEmailAsync(
            string email,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task AddAsync(
            User user,
            CancellationToken cancellationToken = default)
        {
            return repository.AddAsync(user, cancellationToken);
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
