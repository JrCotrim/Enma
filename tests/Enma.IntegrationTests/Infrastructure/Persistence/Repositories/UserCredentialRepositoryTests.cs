using Enma.Application.Abstractions;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Repositories;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Repositories;

[Collection(PostgreSqlCollection.Name)]
public sealed class UserCredentialRepositoryTests(PostgreSqlFixture fixture)
    : IAsyncLifetime
{
    private const string SyntheticPasswordHash =
        "synthetic-opaque-hash-user-credential-repository-001";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        2,
        3,
        4,
        5,
        6,
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
    public async Task AddAsync_ThenUnitOfWorkSave_PersistsCredential()
    {
        User user = new("Credential Repository User", "credential@example.com", CreatedAt);

        await using (EnmaDbContext seedingContext = fixture.CreateDbContext())
        {
            seedingContext.Users.Add(user);
            await seedingContext.SaveChangesAsync();
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        UserCredentialRepository repository = new(dbContext);
        IUnitOfWork unitOfWork = dbContext;
        UserCredential credential = new(user.Id, SyntheticPasswordHash, CreatedAt);

        await repository.AddAsync(credential);

        await using (EnmaDbContext beforeSaveContext = fixture.CreateDbContext())
        {
            bool existsBeforeSave = await beforeSaveContext.UserCredentials
                .AsNoTracking()
                .AnyAsync(candidate => candidate.UserId == user.Id);

            Assert.False(existsBeforeSave);
        }

        await unitOfWork.SaveChangesAsync();

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        UserCredential persistedCredential = await verificationContext.UserCredentials
            .AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == user.Id);

        Assert.Equal(user.Id, persistedCredential.UserId);
        Assert.Equal(SyntheticPasswordHash, persistedCredential.PasswordHash);
        Assert.Equal(CreatedAt, persistedCredential.CreatedAt);
        Assert.Equal(CreatedAt, persistedCredential.PasswordChangedAt);
    }

    [Fact]
    public async Task GetByUserIdAsync_WithExistingCredential_ReturnsTrackedCredential()
    {
        User user = new("Tracked Credential User", "tracked@example.com", CreatedAt);
        UserCredential credential = new(user.Id, SyntheticPasswordHash, CreatedAt);

        await using (EnmaDbContext seedingContext = fixture.CreateDbContext())
        {
            seedingContext.Users.Add(user);
            seedingContext.UserCredentials.Add(credential);
            await seedingContext.SaveChangesAsync();
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        UserCredentialRepository repository = new(dbContext);

        UserCredential? result = await repository.GetByUserIdAsync(user.Id);

        Assert.NotNull(result);
        Assert.Equal(user.Id, result.UserId);
        Assert.Equal(SyntheticPasswordHash, result.PasswordHash);
        Assert.Equal(CreatedAt, result.CreatedAt);
        Assert.Equal(CreatedAt, result.PasswordChangedAt);

        var trackedEntry = Assert.Single(
            dbContext.ChangeTracker.Entries<UserCredential>());
        Assert.Same(result, trackedEntry.Entity);
        Assert.Equal(EntityState.Unchanged, trackedEntry.State);
    }

    [Fact]
    public async Task GetByUserIdAsync_WithMissingCredential_ReturnsNullWithoutTracking()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        UserCredentialRepository repository = new(dbContext);
        Guid missingUserId = Guid.Parse("a5c2ea96-df5a-4472-b985-b4ccba2e2184");

        UserCredential? result = await repository.GetByUserIdAsync(missingUserId);

        Assert.Null(result);
        Assert.Empty(dbContext.ChangeTracker.Entries<UserCredential>());
    }
}
