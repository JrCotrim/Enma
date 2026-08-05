using Enma.Application.Abstractions;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Repositories;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Repositories;

[Collection(PostgreSqlCollection.Name)]
public sealed class UserRepositoryTests(PostgreSqlFixture fixture) : IAsyncLifetime
{
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
    public async Task ExistsByEmailAsync_WithExistingNormalizedEmail_ReturnsTrueWithoutTracking()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = new("Enma User", "USER@EXAMPLE.COM", CreatedAt);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        UserRepository repository = new(dbContext);

        bool exists = await repository.ExistsByEmailAsync("user@example.com");

        Assert.True(exists);
        Assert.Empty(dbContext.ChangeTracker.Entries<User>());
    }

    [Fact]
    public async Task AddAsync_ThenUnitOfWorkSave_PersistsUser()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        UserRepository repository = new(dbContext);
        IUnitOfWork unitOfWork = dbContext;
        User user = new("  Enma User  ", "  USER@EXAMPLE.COM  ", CreatedAt);

        await repository.AddAsync(user);

        await using (EnmaDbContext beforeSaveContext = fixture.CreateDbContext())
        {
            bool existsBeforeSave = await beforeSaveContext.Users
                .AsNoTracking()
                .AnyAsync(candidate => candidate.Id == user.Id);

            Assert.False(existsBeforeSave);
        }

        await unitOfWork.SaveChangesAsync();

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        User persistedUser = await verificationContext.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id);

        Assert.Equal(user.Id, persistedUser.Id);
        Assert.Equal("Enma User", persistedUser.Name);
        Assert.Equal("user@example.com", persistedUser.Email);
        Assert.True(persistedUser.IsActive);
        Assert.Equal(CreatedAt, persistedUser.CreatedAt);
    }
}
